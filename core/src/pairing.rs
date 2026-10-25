//! Pairing two devices with a short code, with no accounts or servers of our own.
//!
//! The code is the whole secret. Both sides derive two things from it:
//! - a throwaway iroh identity. The device showing the code listens as that identity, so the
//!   other side can find it through iroh's normal address lookup just by knowing the code;
//! - a MAC key. Each side proves it knows the code and binds that proof to its real, permanent
//!   identity (which the QUIC/TLS handshake already authenticates).
//!
//! After that, each side remembers the other's permanent identity and the code is useless.

use std::time::Duration;

use anyhow::{Context, Result, bail};
use data_encoding::HEXLOWER;
use hmac::{Hmac, KeyInit, Mac};
use iroh::{
    Endpoint, EndpointId, SecretKey,
    endpoint::{Connection, presets},
    protocol::{AcceptError, ProtocolHandler, Router},
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tokio::sync::mpsc;

use crate::store::{Peer, Store, computer_name};

pub const ALPN: &[u8] = b"async/pair/1";
/// Unambiguous characters only (no 0/O, 1/I/L, U).
const ALPHABET: &[u8] = b"ABCDEFGHJKMNPQRSTVWXYZ23456789";
pub const CODE_LIFETIME: Duration = Duration::from_secs(600);
const JOIN_TIMEOUT: Duration = Duration::from_secs(60);

#[derive(Serialize, Deserialize)]
struct Hello {
    id: String,
    name: String,
    proof: String,
}

pub fn new_code() -> String {
    let random = SecretKey::generate().to_bytes();
    let chars: String = random.iter().take(8).map(|b| ALPHABET[*b as usize % ALPHABET.len()] as char).collect();
    format!("{}-{}", &chars[..4], &chars[4..])
}

fn normalize(code: &str) -> String {
    code.chars().filter(|c| c.is_ascii_alphanumeric()).map(|c| c.to_ascii_uppercase()).collect()
}

fn derive(label: &str, code: &str) -> [u8; 32] {
    Sha256::digest(format!("async-pair-{label}-v1:{}", normalize(code))).into()
}

fn proof(code: &str, role: &str, mine: &EndpointId, theirs: &EndpointId) -> String {
    let mut mac = Hmac::<Sha256>::new_from_slice(&derive("mac", code)).expect("any key length works");
    mac.update(format!("{role}:{mine}:{theirs}").as_bytes());
    HEXLOWER.encode(&mac.finalize().into_bytes())
}

fn check(code: &str, role: &str, claimed: &EndpointId, other: &EndpointId, got: &str) -> bool {
    let mut mac = Hmac::<Sha256>::new_from_slice(&derive("mac", code)).expect("any key length works");
    mac.update(format!("{role}:{claimed}:{other}").as_bytes());
    HEXLOWER.decode(got.as_bytes()).map(|bytes| mac.verify_slice(&bytes).is_ok()).unwrap_or(false)
}

fn rendezvous_key(code: &str) -> SecretKey {
    SecretKey::from_bytes(&derive("endpoint", code))
}

/// Shows-the-code side. Listens on the code's throwaway identity until one device pairs or the
/// code expires. Returns the paired device.
pub async fn host(code: String, me: EndpointId, store: std::sync::Arc<Store>) -> Result<Peer> {
    let endpoint = Endpoint::builder(presets::N0)
        .secret_key(rendezvous_key(&code))
        .alpns(vec![ALPN.to_vec()])
        .bind()
        .await
        .context("starting pairing listener")?;
    let (done_tx, mut done_rx) = mpsc::channel(1);
    let router = Router::builder(endpoint).accept(ALPN, PairHost { code, me, store, done: done_tx }).spawn();

    let result = tokio::time::timeout(CODE_LIFETIME, done_rx.recv()).await;
    router.shutdown().await.ok();
    match result {
        Ok(Some(peer)) => Ok(peer),
        _ => bail!("the pairing code expired"),
    }
}

#[derive(Debug, Clone)]
struct PairHost {
    code: String,
    me: EndpointId,
    store: std::sync::Arc<Store>,
    done: mpsc::Sender<Peer>,
}

impl ProtocolHandler for PairHost {
    async fn accept(&self, conn: Connection) -> Result<(), AcceptError> {
        match self.handle(&conn).await {
            Ok(peer) => {
                eprintln!("paired with {} ({})", peer.name, peer.id);
                self.done.send(peer).await.ok();
            }
            Err(e) => eprintln!("pairing attempt from {} rejected: {e:#}", conn.remote_id()),
        }
        conn.closed().await;
        Ok(())
    }
}

impl PairHost {
    async fn handle(&self, conn: &Connection) -> Result<Peer> {
        let (mut send, mut recv) = conn.accept_bi().await?;
        let hello: Hello = serde_json::from_slice(&recv.read_to_end(4096).await?)?;
        let them = conn.remote_id();
        if hello.id != them.to_string() {
            bail!("claimed identity doesn't match the connection");
        }
        let rendezvous = rendezvous_key(&self.code).public();
        if !check(&self.code, "client", &them, &rendezvous, &hello.proof) {
            send.write_all(b"{\"error\":\"wrong code\"}").await.ok();
            send.finish().ok();
            bail!("wrong code");
        }
        let reply = Hello { id: self.me.to_string(), name: computer_name(), proof: proof(&self.code, "host", &self.me, &them) };
        send.write_all(&serde_json::to_vec(&reply)?).await?;
        send.finish()?;
        self.store.add_peer(&them, &hello.name)
    }
}

/// Types-the-code side. Finds the other device by the code and pairs with it.
pub async fn join(endpoint: &Endpoint, code: &str, store: &Store) -> Result<Peer> {
    if normalize(code).len() != 8 {
        bail!("pairing codes look like ABCD-EFGH");
    }
    let rendezvous = rendezvous_key(code).public();
    let me = endpoint.id();
    let deadline = tokio::time::Instant::now() + JOIN_TIMEOUT;
    // The other side's address takes a few seconds to be published, so keep trying for a while.
    let conn = loop {
        match endpoint.connect(rendezvous, ALPN).await {
            Ok(conn) => break conn,
            Err(e) if tokio::time::Instant::now() < deadline => {
                eprintln!("pairing: not found yet ({e}), retrying");
                tokio::time::sleep(Duration::from_secs(2)).await;
            }
            Err(e) => bail!("couldn't find a device showing that code ({e})"),
        }
    };

    let (mut send, mut recv) = conn.open_bi().await?;
    let hello = Hello { id: me.to_string(), name: computer_name(), proof: proof(code, "client", &me, &rendezvous) };
    send.write_all(&serde_json::to_vec(&hello)?).await?;
    send.finish()?;
    let reply = recv.read_to_end(4096).await?;
    conn.close(0u32.into(), b"done");

    if let Ok(err) = serde_json::from_slice::<serde_json::Value>(&reply) {
        if let Some(e) = err.get("error").and_then(|e| e.as_str()) {
            bail!("the other device said: {e}");
        }
    }
    let reply: Hello = serde_json::from_slice(&reply).context("unexpected reply")?;
    let them: EndpointId = reply.id.parse().context("bad identity in reply")?;
    if !check(code, "host", &them, &me, &reply.proof) {
        bail!("the other device couldn't prove it knows the code");
    }
    store.add_peer(&them, &reply.name)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn codes_are_formatted_and_normalized() {
        let code = new_code();
        assert_eq!(code.len(), 9);
        assert_eq!(normalize(&code.to_lowercase().replace('-', " ")), normalize(&code));
    }

    #[test]
    fn proofs_verify_only_with_the_right_code() {
        let a = SecretKey::generate().public();
        let b = SecretKey::generate().public();
        let p = proof("ABCD-EFGH", "client", &a, &b);
        assert!(check("abcd efgh", "client", &a, &b, &p));
        assert!(!check("ABCD-EFGJ", "client", &a, &b, &p));
        assert!(!check("ABCD-EFGH", "host", &a, &b, &p));
    }
}
