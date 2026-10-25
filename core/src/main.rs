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
//! async-core [--data DIR] tunnel PEER [rdp]  # open a local port into PEER and keep it open
//! async-core [--data DIR] control            # JSON over stdin/stdout (what the app uses)
//! ```

mod control;
mod pairing;
mod store;
mod tunnel;

use std::{net::SocketAddr, path::PathBuf, sync::Arc};

use anyhow::{Context, Result, bail};
use iroh::{Endpoint, EndpointId, endpoint::presets, protocol::Router};

use crate::{store::Store, tunnel::{Service, TunnelHost, Tunnels}};

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

    async fn start(data: PathBuf, rdp_target: SocketAddr, allow_incoming: bool) -> Result<Self> {
        let store = Arc::new(Store::open(&data)?);
        let endpoint = Endpoint::builder(presets::N0)
            .secret_key(store.secret_key()?)
            .alpns(vec![tunnel::ALPN.to_vec()])
            .bind()
            .await
            .context("starting network endpoint")?;
        let host = TunnelHost { store: store.clone(), rdp_target, allow_incoming };
        let router = Router::builder(endpoint.clone()).accept(tunnel::ALPN, host).spawn();
        Ok(Self { router, store, tunnels: Tunnels::new(endpoint) })
    }
}

fn default_data_dir() -> PathBuf {
    let base = std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".config")))
        .unwrap_or_else(|| PathBuf::from("."));
    base.join("Async").join("core")
}

#[tokio::main]
async fn main() -> Result<()> {
    let mut args: Vec<String> = std::env::args().skip(1).collect();
    let mut data = default_data_dir();
    let mut rdp_target: SocketAddr = "127.0.0.1:3389".parse()?;
    let mut allow_incoming = true;
    while let Some(i) = args.iter().position(|a| a.starts_with("--")) {
        let flag = args.remove(i);
        match flag.as_str() {
            "--data" => data = PathBuf::from(args.get(i).cloned().context("--data needs a folder")?),
            "--rdp" => rdp_target = args.get(i).context("--rdp needs host:port")?.parse()?,
            "--no-incoming" => { allow_incoming = false; continue; }
            "--version" => { println!("async-core {}", env!("CARGO_PKG_VERSION")); return Ok(()); }
            _ => bail!("unknown option {flag}"),
        }
        args.remove(i);
    }

    let core = Arc::new(Core::start(data, rdp_target, allow_incoming).await?);
    let mode = args.first().map(String::as_str).unwrap_or("control");
    match mode {
        "control" => control::run(core).await,
        "serve" => {
            println!("this device: {} ({})", core.id(), store::computer_name());
            println!("letting paired devices in; Ctrl+C to stop");
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
            let service = Service::parse(args.get(2).map(String::as_str).unwrap_or("rdp"))?;
            let addr = core.tunnels.open(peer, service).await?;
            println!("tunnel open: {addr}; Ctrl+C to close");
            tokio::signal::ctrl_c().await?;
            Ok(())
        }
        other => bail!("unknown mode {other:?}"),
    }
}
