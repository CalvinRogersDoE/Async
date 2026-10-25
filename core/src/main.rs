//! async-core: the part of Async that runs on every computer.
//!
//! It owns this device's identity, pairs with other devices using short codes, lets paired
//! devices into this one, and opens tunnels into them. The Async app drives it over stdin/stdout
//! with one JSON object per line (see `control`). For testing and headless machines it also has
//! plain command-line modes:
//!
//! ```text
//! async-core [--data DIR] serve              # let paired devices in, nothing else
//! async-core [--data DIR] pair-host          # show a code and wait for another device
//! async-core [--data DIR] pair-join CODE     # pair with the device showing CODE
//! async-core [--data DIR] peers              # list paired devices
//! async-core [--data DIR] tunnel PEER [rdp|vnc]  # open a local port into PEER and keep it open
//! async-core [--data DIR] describe PEER      # what PEER is and which services it offers
//! async-core [--data DIR] control            # JSON over stdin/stdout (what the app uses)
//! ```
//!
//! What this device offers to paired devices defaults to the OS's own screen sharing (Remote
//! Desktop on Windows and Linux, Screen Sharing/VNC on macOS). Override with `--rdp HOST:PORT`,
//! `--vnc HOST:PORT`, `--no-rdp`, `--no-vnc`, or refuse everyone with `--no-incoming`.

mod control;
mod pairing;
mod store;
mod tunnel;

use std::{path::PathBuf, sync::Arc};

use anyhow::{Context, Result, bail};
use iroh::{Endpoint, EndpointId, endpoint::presets, protocol::Router};

use crate::{store::Store, tunnel::{HostConfig, Service, TunnelHost, Tunnels}};

pub struct Core {
    pub router: Router,
    pub store: Arc<Store>,
    pub tunnels: Tunnels,
}

impl Core {
    pub fn endpoint(&self) -> &Endpoint {
        self.router.endpoint()
    }

    pub fn id(&self) -> EndpointId {
        self.endpoint().id()
    }

    async fn start(data: PathBuf, config: HostConfig) -> Result<Self> {
        let store = Arc::new(Store::open(&data)?);
        let endpoint = Endpoint::builder(presets::N0)
            .secret_key(store.secret_key()?)
            .alpns(vec![tunnel::ALPN.to_vec()])
            .bind()
            .await
            .context("starting network endpoint")?;
        let host = TunnelHost { store: store.clone(), config };
        let router = Router::builder(endpoint.clone()).accept(tunnel::ALPN, host).spawn();
        Ok(Self { router, store, tunnels: Tunnels::new(endpoint) })
    }
}

/// Where the identity and paired devices live: %APPDATA%\Async\core on Windows,
/// ~/Library/Application Support/Async/core on macOS, $XDG_CONFIG_HOME (or ~/.config)/async/core on Linux.
fn default_data_dir() -> PathBuf {
    let home = || std::env::var_os("HOME").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("."));
    let base = if cfg!(windows) {
        std::env::var_os("APPDATA").map(PathBuf::from).unwrap_or_else(home).join("Async")
    } else if cfg!(target_os = "macos") {
        home().join("Library/Application Support/Async")
    } else {
        std::env::var_os("XDG_CONFIG_HOME").map(PathBuf::from).unwrap_or_else(|| home().join(".config")).join("async")
    };
    base.join("core")
}

#[tokio::main]
async fn main() -> Result<()> {
    let mut args: Vec<String> = std::env::args().skip(1).collect();
    let mut data = default_data_dir();
    let mut config = HostConfig::for_this_os();
    while let Some(i) = args.iter().position(|a| a.starts_with("--")) {
        let flag = args.remove(i);
        match flag.as_str() {
            "--data" => data = PathBuf::from(args.get(i).cloned().context("--data needs a folder")?),
            "--rdp" => config.rdp = Some(args.get(i).context("--rdp needs host:port")?.parse()?),
            "--vnc" => config.vnc = Some(args.get(i).context("--vnc needs host:port")?.parse()?),
            "--no-rdp" => { config.rdp = None; continue; }
            "--no-vnc" => { config.vnc = None; continue; }
            "--no-incoming" => { config.allow_incoming = false; continue; }
            "--version" => { println!("async-core {}", env!("CARGO_PKG_VERSION")); return Ok(()); }
            _ => bail!("unknown option {flag}"),
        }
        args.remove(i);
    }

    let offers = config.clone();
    let core = Arc::new(Core::start(data, config).await?);
    let mode = args.first().map(String::as_str).unwrap_or("control");
    match mode {
        "control" => control::run(core).await,
        "serve" => {
            let info = offers.info().await;
            println!("this device: {} ({}, {})", core.id(), info.name, info.os);
            if info.services.is_empty() {
                println!("warning: no screen sharing is listening here yet (see README: hosting on Linux/macOS)");
            } else {
                println!("offering to paired devices: {}", info.services.join(", "));
            }
            println!("Ctrl+C to stop");
            tokio::signal::ctrl_c().await?;
            Ok(())
        }
        "pair-host" => {
            let code = pairing::new_code();
            println!("pairing code: {code} (valid for 10 minutes)");
            let peer = pairing::host(code, core.id(), core.store.clone()).await?;
            println!("paired with {} ({})", peer.name, peer.id);
            Ok(())
        }
        "pair-join" => {
            let code = args.get(1).context("usage: pair-join CODE")?;
            let peer = pairing::join(core.endpoint(), code, &core.store).await?;
            println!("paired with {} ({})", peer.name, peer.id);
            Ok(())
        }
        "peers" => {
            for p in core.store.peers() {
                println!("{}  {}", p.id, p.name);
            }
            Ok(())
        }
        "tunnel" => {
            let peer: EndpointId = args.get(1).context("usage: tunnel PEER [rdp]")?.parse().context("not a device id")?;
            let service = match args.get(2) {
                Some(name) => Service::parse(name)?,
                None => best_service(&core.tunnels.describe(peer).await?.services)?,
            };
            let addr = core.tunnels.open(peer, service).await?;
            println!("tunnel open: {addr}; Ctrl+C to close");
            tokio::signal::ctrl_c().await?;
            Ok(())
        }
        "describe" => {
            let peer: EndpointId = args.get(1).context("usage: describe PEER")?.parse().context("not a device id")?;
            println!("{}", serde_json::to_string_pretty(&core.tunnels.describe(peer).await?)?);
            Ok(())
        }
        other => bail!("unknown mode {other:?}"),
    }
}

/// Remote Desktop when the device offers it (better quality), otherwise VNC.
pub fn best_service(services: &[String]) -> Result<Service> {
    if services.iter().any(|s| s == "rdp") {
        Ok(Service::RemoteDesktop)
    } else if services.iter().any(|s| s == "vnc") {
        Ok(Service::Vnc)
    } else {
        bail!("that device has no screen sharing turned on")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};

    /// Two devices pair with a code, one asks what the other offers, then sends data through a
    /// tunnel to a stand-in service and gets it back. Needs internet (iroh's relays and lookup).
    #[tokio::test(flavor = "multi_thread")]
    async fn pair_describe_and_tunnel() -> Result<()> {
        let echo = tokio::net::TcpListener::bind("127.0.0.1:0").await?;
        let echo_addr = echo.local_addr()?;
        tokio::spawn(async move {
            while let Ok((mut s, _)) = echo.accept().await {
                tokio::spawn(async move {
                    let (mut r, mut w) = s.split();
                    let _ = tokio::io::copy(&mut r, &mut w).await;
                });
            }
        });

        let dir = std::env::temp_dir().join(format!("async-core-test-{}", std::process::id()));
        let host = Core::start(dir.join("host"), HostConfig { rdp: Some(echo_addr), vnc: None, allow_incoming: true }).await?;
        let viewer = Core::start(dir.join("viewer"), HostConfig { rdp: None, vnc: None, allow_incoming: true }).await?;

        let code = pairing::new_code();
        let showing = tokio::spawn(pairing::host(code.clone(), host.id(), host.store.clone()));
        let paired = pairing::join(viewer.endpoint(), &code, &viewer.store).await?;
        assert_eq!(paired.id, host.id().to_string());
        assert_eq!(showing.await??.id, viewer.id().to_string());

        let info = viewer.tunnels.describe(host.id()).await?;
        assert_eq!(info.services, vec!["rdp".to_string()]);
        assert_eq!(best_service(&info.services)?, Service::RemoteDesktop);

        let addr = viewer.tunnels.open(host.id(), Service::RemoteDesktop).await?;
        let mut tcp = tokio::net::TcpStream::connect(addr).await?;
        let sent: Vec<u8> = (0..200_000u32).map(|i| (i * 7 % 251) as u8).collect();
        tcp.write_all(&sent).await?;
        let mut got = vec![0u8; sent.len()];
        tcp.read_exact(&mut got).await?;
        assert_eq!(got, sent);

        std::fs::remove_dir_all(&dir).ok();
        Ok(())
    }
}
