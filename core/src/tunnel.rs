//! Carrying TCP services (Remote Desktop for now) between paired devices over iroh.
//!
//! The viewing side opens a local port on 127.0.0.2 (Windows' Remote Desktop client refuses to
//! connect to 127.0.0.1 because it looks like "this computer"). Each TCP connection to it becomes
//! one QUIC stream to the other device, which connects it to its own local service.

use std::{collections::HashMap, net::SocketAddr, sync::Arc};

use anyhow::{Context, Result, bail};
use iroh::{
    Endpoint, EndpointId,
    endpoint::Connection,
    protocol::{AcceptError, ProtocolHandler},
};
use tokio::{
    net::{TcpListener, TcpStream},
    sync::Mutex,
};

use crate::store::Store;

pub const ALPN: &[u8] = b"async/tunnel/1";

/// Services a device can offer through the tunnel. The first byte of every stream picks one.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum Service {
    RemoteDesktop = 1,
}

impl Service {
    pub fn parse(name: &str) -> Result<Self> {
        match name {
            "rdp" => Ok(Self::RemoteDesktop),
            _ => bail!("unknown service {name:?}"),
        }
    }

    fn from_byte(b: u8) -> Option<Self> {
        (b == 1).then_some(Self::RemoteDesktop)
    }
}

// ---------- the side being viewed ----------

/// Accepts tunnel connections, but only from paired devices.
#[derive(Debug, Clone)]
pub struct TunnelHost {
    pub store: Arc<Store>,
    pub rdp_target: SocketAddr,
    pub allow_incoming: bool,
}

impl ProtocolHandler for TunnelHost {
    async fn accept(&self, conn: Connection) -> Result<(), AcceptError> {
        let them = conn.remote_id();
        if !self.allow_incoming || !self.store.is_paired(&them) {
            eprintln!("refused tunnel from unpaired device {them}");
            conn.close(1u32.into(), b"not paired");
            return Ok(());
        }
        eprintln!("tunnel opened by {them}");
        while let Ok((send, mut recv)) = conn.accept_bi().await {
            let target = self.rdp_target;
            tokio::spawn(async move {
                let mut kind = [0u8; 1];
                if recv.read_exact(&mut kind).await.is_err() {
                    return;
                }
                match Service::from_byte(kind[0]) {
                    Some(Service::RemoteDesktop) => {
                        if let Err(e) = pipe_to(target, send, recv).await {
                            eprintln!("remote desktop stream ended: {e:#}");
                        }
                    }
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
            return Ok(SocketAddr::from(([127, 0, 0, 2], *port)));
        }
        // Connect once up front so a bad peer fails here, not inside Remote Desktop.
        connection(&self.endpoint, &self.conns, peer).await?;
        let listener = TcpListener::bind("127.0.0.2:0").await.context("opening local tunnel port")?;
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
