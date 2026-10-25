//! What a device remembers between runs: its own identity key and the devices it has paired with.

use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use iroh::{EndpointId, SecretKey};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Peer {
    /// The other device's public key; it is also how we reach it.
    pub id: String,
    /// Its computer name, as it told us during pairing.
    pub name: String,
    pub paired_at: u64,
}

#[derive(Debug)]
pub struct Store {
    dir: PathBuf,
}

impl Store {
    pub fn open(dir: &Path) -> Result<Self> {
        std::fs::create_dir_all(dir).with_context(|| format!("creating {}", dir.display()))?;
        Ok(Self { dir: dir.to_path_buf() })
    }

    /// This device's identity. Created on first run and kept forever; deleting it un-pairs everything.
    pub fn secret_key(&self) -> Result<SecretKey> {
        let path = self.dir.join("identity.key");
        if let Ok(bytes) = std::fs::read(&path) {
            if let Ok(bytes) = <[u8; 32]>::try_from(bytes.as_slice()) {
                return Ok(SecretKey::from_bytes(&bytes));
            }
        }
        let key = SecretKey::generate();
        std::fs::write(&path, key.to_bytes()).context("saving identity key")?;
        Ok(key)
    }

    pub fn peers(&self) -> Vec<Peer> {
        std::fs::read(self.dir.join("peers.json"))
            .ok()
            .and_then(|b| serde_json::from_slice(&b).ok())
            .unwrap_or_default()
    }

    pub fn is_paired(&self, id: &EndpointId) -> bool {
        let id = id.to_string();
        self.peers().iter().any(|p| p.id == id)
    }

    pub fn add_peer(&self, id: &EndpointId, name: &str) -> Result<Peer> {
        let mut peers = self.peers();
        peers.retain(|p| p.id != id.to_string());
        let peer = Peer { id: id.to_string(), name: name.to_string(), paired_at: now() };
        peers.push(peer.clone());
        self.save(&peers)?;
        Ok(peer)
    }

    pub fn remove_peer(&self, id: &str) -> Result<bool> {
        let mut peers = self.peers();
        let before = peers.len();
        peers.retain(|p| p.id != id);
        self.save(&peers)?;
        Ok(peers.len() != before)
    }

    fn save(&self, peers: &[Peer]) -> Result<()> {
        let tmp = self.dir.join("peers.json.tmp");
        std::fs::write(&tmp, serde_json::to_vec_pretty(peers)?)?;
        std::fs::rename(&tmp, self.dir.join("peers.json"))?;
        Ok(())
    }
}

fn now() -> u64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

pub fn computer_name() -> String {
    // Background services (systemd, launchd) don't get HOSTNAME, so ask the system as well.
    let ask = |cmd: &str, args: &[&str]| {
        std::process::Command::new(cmd).args(args).output().ok().and_then(|o| String::from_utf8(o.stdout).ok())
    };
    std::env::var("COMPUTERNAME")
        .ok()
        .or_else(|| cfg!(target_os = "macos").then(|| ask("scutil", &["--get", "ComputerName"])).flatten())
        .or_else(|| std::fs::read_to_string("/etc/hostname").ok())
        .or_else(|| std::env::var("HOSTNAME").ok())
        .or_else(|| ask("hostname", &[]))
        .map(|n| n.trim().to_string())
        .filter(|n| !n.is_empty())
        .unwrap_or_else(|| "Unnamed PC".into())
}
