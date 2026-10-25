//! Carrying TCP services between paired devices over iroh: Remote Desktop (Windows, and Linux
//! via GNOME Remote Desktop or xrdp) and VNC (macOS Screen Sharing, or any VNC server).
//!
//! The viewing side opens a local port on 127.0.0.2 on Windows (its Remote Desktop client refuses
//! 127.0.0.1 because it looks like "this computer"; elsewhere 127.0.0.1). Each TCP connection to
//! it becomes one QUIC stream to the other device, which connects it to its own local service.
//! The first byte of every stream says which service; byte 0 asks the device what it offers.

use std::{collections::HashMap, net::SocketAddr, sync::Arc};

use anyhow::{Context, Result, bail};
use iroh::{
    Endpoint, EndpointId,
    endpoint::Connection,
    protocol::{AcceptError, ProtocolHandler},
};
use serde::{Deserialize, Serialize};
use tokio::{
    net::{TcpListener, TcpStream},
    sync::Mutex,
};

use crate::store::{Store, computer_name};

pub const ALPN: &[u8] = b"async/tunnel/1";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum Service {
    /// Not a tunnel: asks the device which services it offers.
    Info = 0,
    RemoteDesktop = 1,
    Vnc = 2,
}

impl Service {
    pub fn parse(name: &str) -> Result<Self> {
        match name {
            "rdp" => Ok(Self::RemoteDesktop),
            "vnc" => Ok(Self::Vnc),
            _ => bail!("unknown service {name:?}"),
        }
    }

    pub fn name(self) -> &'static str {
        match self {
            Self::Info => "info",
            Self::RemoteDesktop => "rdp",
            Self::Vnc => "vnc",
        }
    }

    fn from_byte(b: u8) -> Option<Self> {
        match b {
            0 => Some(Self::Info),
            1 => Some(Self::RemoteDesktop),
            2 => Some(Self::Vnc),
            _ => None,
        }
    }
}

/// What a device tells paired devices about itself.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DeviceInfo {
    pub name: String,
    pub os: String,
    pub services: Vec<String>,
    pub version: String,
    /// The device's monitors, so a viewer can lay a remote session out the same way and show one
    /// of them. Empty when unknown (older versions, or screen sharing that shows one picture).
    #[serde(default)]
    pub screens: Vec<crate::screens::Screen>,
}

/// Which local services this device lets paired devices into.
#[derive(Debug, Clone)]
pub struct HostConfig {
    pub rdp: Option<SocketAddr>,
    pub vnc: Option<SocketAddr>,
    pub allow_incoming: bool,
}

impl HostConfig {
    /// Each OS's built-in screen sharing: Remote Desktop on Windows and Linux (GNOME Remote
    /// Desktop / xrdp listen on 3389), Screen Sharing (VNC, 5900) on macOS.
    pub fn for_this_os() -> Self {
        let rdp: SocketAddr = "127.0.0.1:3389".parse().unwrap();
        let vnc: SocketAddr = "127.0.0.1:5900".parse().unwrap();
        match std::env::consts::OS {
            "macos" => Self { rdp: None, vnc: Some(vnc), allow_incoming: true },
            "linux" => Self { rdp: Some(rdp), vnc: Some(vnc), allow_incoming: true },
            _ => Self { rdp: Some(rdp), vnc: None, allow_incoming: true },
        }
    }

    fn target(&self, service: Service) -> Option<SocketAddr> {
        match service {
            Service::RemoteDesktop => self.rdp,
            Service::Vnc => self.vnc,
            Service::Info => None,
        }
    }

    /// Services that are configured and actually listening right now.
    pub async fn info(&self) -> DeviceInfo {
        let mut services = Vec::new();
        for s in [Service::RemoteDesktop, Service::Vnc] {
            if let Some(addr) = self.target(s) {
                if TcpStream::connect(addr).await.is_ok() {
                    services.push(s.name().to_string());
                }
            }
        }
        DeviceInfo {
            name: computer_name(),
            os: std::env::consts::OS.to_string(),
            services,
            version: env!("CARGO_PKG_VERSION").to_string(),
            screens: crate::screens::list(),
        }
    }
}

// ---------- the side being viewed ----------

/// Accepts tunnel connections, but only from paired devices.
#[derive(Debug, Clone)]
pub struct TunnelHost {
    pub store: Arc<Store>,
    pub config: HostConfig,
}

impl ProtocolHandler for TunnelHost {
    async fn accept(&self, conn: Connection) -> Result<(), AcceptError> {
        let them = conn.remote_id();
        if !self.config.allow_incoming || !self.store.is_paired(&them) {
            eprintln!("refused tunnel from unpaired device {them}");
            conn.close(1u32.into(), b"not paired");
            return Ok(());
        }
        eprintln!("tunnel opened by {them}");
        while let Ok((mut send, mut recv)) = conn.accept_bi().await {
            let config = self.config.clone();
            tokio::spawn(async move {
                let mut kind = [0u8; 1];
                if recv.read_exact(&mut kind).await.is_err() {
                    return;
                }
                match Service::from_byte(kind[0]) {
                    Some(Service::Info) => {
                        let info = serde_json::to_vec(&config.info().await).unwrap_or_default();
                        let _ = send.write_all(&info).await;
                        let _ = send.finish();
                    }
                    Some(service) => match config.target(service) {
                        Some(target) => {
                            if let Err(e) = pipe_to(target, send, recv).await {
                                eprintln!("{} stream ended: {e:#}", service.name());
                            }
                        }
                        None => eprintln!("{} isn't offered on this device", service.name()),
                    },
                    None => eprintln!("unknown service {}", kind[0]),
                }
            });
        }
        eprintln!("tunnel from {them} closed");
        Ok(())
    }
}

async fn pipe_to(target: SocketAddr, send: iroh::endpoint::SendStream, recv: iroh::endpoint::RecvStream) -> Result<()> {
    let mut tcp = TcpStream::connect(target).await.with_context(|| format!("connecting to {target}"))?;
    tcp.set_nodelay(true).ok();
    let mut quic = tokio::io::join(recv, send);
    tokio::io::copy_bidirectional(&mut quic, &mut tcp).await?;
    Ok(())
}

// ---------- the viewing side ----------

/// Local ports forwarding to paired devices, and the QUIC connections behind them.
pub struct Tunnels {
    endpoint: Endpoint,
    ports: Mutex<HashMap<(EndpointId, Service), u16>>,
    conns: Arc<Mutex<HashMap<EndpointId, Connection>>>,
}

impl Tunnels {
    pub fn new(endpoint: Endpoint) -> Self {
        Self { endpoint, ports: Mutex::default(), conns: Arc::default() }
    }

    /// The local port that leads to `service` on `peer`, opening it if needed.
    pub async fn open(&self, peer: EndpointId, service: Service) -> Result<SocketAddr> {
        let mut ports = self.ports.lock().await;
        if let Some(port) = ports.get(&(peer, service)) {
            return Ok(SocketAddr::new(local_bind().ip(), *port));
        }
        // Connect once up front so a bad peer fails here, not inside Remote Desktop.
        connection(&self.endpoint, &self.conns, peer).await?;
        let listener = TcpListener::bind(local_bind()).await.context("opening local tunnel port")?;
        let addr = listener.local_addr()?;
        ports.insert((peer, service), addr.port());

        let endpoint = self.endpoint.clone();
        let conns = self.conns.clone();
        tokio::spawn(async move {
            while let Ok((tcp, _)) = listener.accept().await {
                let (endpoint, conns) = (endpoint.clone(), conns.clone());
                tokio::spawn(async move {
                    if let Err(e) = forward(&endpoint, &conns, peer, service, tcp).await {
                        eprintln!("tunnel stream to {peer} ended: {e:#}");
                    }
                });
            }
        });
        Ok(addr)
    }

    /// Asks a paired device what it is and which services it offers.
    pub async fn describe(&self, peer: EndpointId) -> Result<DeviceInfo> {
        let conn = connection(&self.endpoint, &self.conns, peer).await?;
        let (mut send, mut recv) = conn.open_bi().await?;
        send.write_all(&[Service::Info as u8]).await?;
        send.finish()?;
        let reply = recv.read_to_end(4096).await.context("the device didn't answer")?;
        serde_json::from_slice(&reply).context("unexpected answer from the device")
    }

    /// Round-trip time and whether the link is direct (not through a relay), for status display.
    pub async fn probe(&self, peer: EndpointId) -> Result<(u128, bool)> {
        let started = std::time::Instant::now();
        let conn = connection(&self.endpoint, &self.conns, peer).await?;
        let paths = conn.paths();
        let selected = paths.into_iter().find(|p| p.is_selected());
        let rtt = selected.as_ref().map(|p| p.rtt().as_millis()).unwrap_or_else(|| started.elapsed().as_millis());
        let direct = selected.is_some_and(|p| p.is_ip());
        Ok((rtt, direct))
    }
}

async fn connection(endpoint: &Endpoint, conns: &Mutex<HashMap<EndpointId, Connection>>, peer: EndpointId) -> Result<Connection> {
    let mut conns = conns.lock().await;
    if let Some(c) = conns.get(&peer) {
        if c.close_reason().is_none() {
            return Ok(c.clone());
        }
    }
    let c = endpoint.connect(peer, ALPN).await.context("couldn't reach that device")?;
    conns.insert(peer, c.clone());
    Ok(c)
}

async fn forward(endpoint: &Endpoint, conns: &Mutex<HashMap<EndpointId, Connection>>, peer: EndpointId, service: Service, mut tcp: TcpStream) -> Result<()> {
    tcp.set_nodelay(true).ok();
    let conn = connection(endpoint, conns, peer).await?;
    let (mut send, recv) = conn.open_bi().await?;
    send.write_all(&[service as u8]).await?;
    let mut quic = tokio::io::join(recv, send);
    tokio::io::copy_bidirectional(&mut tcp, &mut quic).await?;
    Ok(())
}

fn local_bind() -> SocketAddr {
    if cfg!(windows) { "127.0.0.2:0".parse().unwrap() } else { "127.0.0.1:0".parse().unwrap() }
}
