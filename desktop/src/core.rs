//! Talks to the async-core service running in the background, over its local socket, with the
//! same JSON-lines protocol the Windows app uses over stdin/stdout (see core/src/control.rs).

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::os::unix::net::UnixStream;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::time::Duration;

use anyhow::{Context, Result, anyhow, bail};
use serde::Deserialize;
use serde_json::{Value, json};

#[derive(Debug, Clone, Deserialize)]
pub struct Device {
    pub id: String,
    pub name: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct DeviceInfo {
    pub os: String,
    pub services: Vec<String>,
    /// The device's monitors (Windows), in desktop pixels with its main one at (0, 0).
    #[serde(default)]
    pub screens: Vec<RemoteScreen>,
    /// The password of screen sharing Async set up itself (x11vnc on Linux), handed only to paired devices.
    #[serde(default)]
    pub vnc_password: Option<String>,
}

#[derive(Debug, Clone, Copy, Deserialize, PartialEq)]
pub struct RemoteScreen {
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
    pub primary: bool,
}

/// Screens in the order they sit on the desk (left to right, then top to bottom); edges and the
/// screen switcher refer to them by their position in this order.
pub fn sorted(mut screens: Vec<RemoteScreen>) -> Vec<RemoteScreen> {
    screens.sort_by_key(|s| (s.x, s.y));
    screens
}

/// "Left", "Middle", "Right" for up to three side-by-side screens, otherwise "Screen 4".
pub fn screen_name(i: usize, count: usize) -> String {
    match (i, count) {
        (0, 2..) => "Left".into(),
        (1, 3) => "Middle".into(),
        (i, n) if i + 1 == n && n >= 2 => "Right".into(),
        (i, _) => format!("Screen {}", i + 1),
    }
}

pub fn screen_label(s: &RemoteScreen, i: usize, count: usize) -> String {
    let main = if s.primary { ", main" } else { "" };
    format!("{} screen ({}×{}{main})", screen_name(i, count), s.w, s.h)
}

type Pending = Arc<Mutex<HashMap<u64, mpsc::Sender<Value>>>>;

pub struct Core {
    stream: Mutex<UnixStream>,
    pending: Pending,
    next: AtomicU64,
    pub name: String,
}

pub fn socket_path() -> PathBuf {
    match std::env::var_os("XDG_RUNTIME_DIR") {
        Some(dir) => PathBuf::from(dir).join("async-core.sock"),
        None => data_dir().join("control.sock"),
    }
}

fn data_dir() -> PathBuf {
    crate::config::folder().join("core")
}

impl Core {
    /// Connects to the service. Events (`paired`, `pair_ended`) go to `events`.
    pub fn connect(events: impl Fn(Value) + Send + 'static) -> Result<Arc<Self>> {
        let path = socket_path();
        let stream = UnixStream::connect(&path).with_context(|| {
            format!("Async's background service isn't running ({}). Run install.sh, or: systemctl --user start async-core", path.display())
        })?;
        let pending: Pending = Arc::default();
        let (ready_tx, ready_rx) = mpsc::channel::<Value>();
        let reader = BufReader::new(stream.try_clone()?);
        let replies = pending.clone();
        std::thread::spawn(move || {
            for line in reader.lines() {
                let Ok(line) = line else { break };
                let Ok(msg) = serde_json::from_str::<Value>(&line) else { continue };
                if let Some(id) = msg.get("id").and_then(Value::as_u64) {
                    if let Some(tx) = replies.lock().unwrap().remove(&id) {
                        let _ = tx.send(msg);
                    }
                } else if msg.get("event").and_then(Value::as_str) == Some("ready") {
                    let _ = ready_tx.send(msg);
                } else {
                    events(msg);
                }
            }
            // The service went away: fail everything still waiting.
            replies.lock().unwrap().clear();
        });
        let ready = ready_rx.recv_timeout(Duration::from_secs(5)).context("Async's background service didn't answer")?;
        let text = |k: &str| ready.get(k).and_then(Value::as_str).unwrap_or_default().to_string();
        Ok(Arc::new(Self { stream: Mutex::new(stream), pending, next: AtomicU64::new(1), name: text("name") }))
    }

    pub fn request(&self, mut req: Value, timeout: Duration) -> Result<Value> {
        let id = self.next.fetch_add(1, Ordering::Relaxed);
        req["id"] = json!(id);
        let (tx, rx) = mpsc::channel();
        self.pending.lock().unwrap().insert(id, tx);
        {
            let mut s = self.stream.lock().unwrap();
            writeln!(s, "{req}").and_then(|_| s.flush()).context("Async's background service went away")?;
        }
        let reply = rx.recv_timeout(timeout).map_err(|_| {
            self.pending.lock().unwrap().remove(&id);
            anyhow!("Async's background service didn't answer in time")
        })?;
        if reply.get("ok").and_then(Value::as_bool) != Some(true) {
            bail!("{}", reply.get("error").and_then(Value::as_str).unwrap_or("something went wrong"));
        }
        Ok(reply)
    }

    pub fn peers(&self) -> Result<Vec<Device>> {
        let r = self.request(json!({ "cmd": "peers" }), Duration::from_secs(5))?;
        Ok(serde_json::from_value(r["peers"].clone())?)
    }

    pub fn describe(&self, peer: &str) -> Result<DeviceInfo> {
        let r = self.request(json!({ "cmd": "describe", "peer": peer }), Duration::from_secs(20))?;
        Ok(serde_json::from_value(r)?)
    }

    /// Opens a tunnel into the device's screen sharing: (local host, local port, "rdp" or "vnc").
    pub fn open_screen(&self, peer: &str) -> Result<(String, u16, String)> {
        let r = self.request(json!({ "cmd": "tunnel", "peer": peer }), Duration::from_secs(40))?;
        let host = r["host"].as_str().unwrap_or("127.0.0.1").to_string();
        let port = r["port"].as_u64().context("no port in the tunnel reply")? as u16;
        let service = r["service"].as_str().unwrap_or("rdp").to_string();
        Ok((host, port, service))
    }

    /// Opens a tunnel to one of Async's own services on the device ("input" or "files"): (host, port).
    pub fn open_service(&self, peer: &str, service: &str) -> Result<(String, u16)> {
        let r = self.request(json!({ "cmd": "tunnel", "peer": peer, "service": service }), Duration::from_secs(40))?;
        let host = r["host"].as_str().unwrap_or("127.0.0.1").to_string();
        let port = r["port"].as_u64().context("no port in the tunnel reply")? as u16;
        Ok((host, port))
    }

    /// Paired devices showing this computer in a portal right now.
    pub fn viewers(&self) -> Result<Vec<String>> {
        let r = self.request(json!({ "cmd": "viewers" }), Duration::from_secs(5))?;
        Ok(r["viewers"].as_array().map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect()).unwrap_or_default())
    }

    pub fn pair_host(&self) -> Result<(String, u64)> {
        let r = self.request(json!({ "cmd": "pair_host" }), Duration::from_secs(10))?;
        Ok((r["code"].as_str().unwrap_or_default().to_string(), r["expires_in"].as_u64().unwrap_or(600)))
    }

    pub fn pair_cancel(&self) {
        let _ = self.request(json!({ "cmd": "pair_cancel" }), Duration::from_secs(5));
    }

    pub fn pair_join(&self, code: &str) -> Result<Device> {
        let r = self.request(json!({ "cmd": "pair_join", "code": code }), Duration::from_secs(90))?;
        Ok(serde_json::from_value(r["peer"].clone())?)
    }

    pub fn remove(&self, peer: &str) -> Result<()> {
        self.request(json!({ "cmd": "remove_peer", "peer": peer }), Duration::from_secs(5))?;
        Ok(())
    }
}
