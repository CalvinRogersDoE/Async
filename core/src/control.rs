//! The JSON-lines protocol the Async app speaks to async-core: over stdin/stdout when the app
//! runs async-core itself (Windows), or over a local socket to the background service (macOS and
//! Linux, see `listen`).
//!
//! Requests carry an `id` that the reply echoes; events have an `event` field instead.
//!
//! | cmd          | fields          | reply                                    |
//! |--------------|-----------------|------------------------------------------|
//! | `info`       |                 | `node_id`, `name`, `version`             |
//! | `peers`      |                 | `peers: [{id, name, paired_at}]`         |
//! | `pair_host`  |                 | `code`, `expires_in`; later event `paired` or `pair_ended` |
//! | `pair_cancel`|                 |                                          |
//! | `pair_join`  | `code`          | `peer`                                   |
//! | `remove_peer`| `peer`          | `removed`                                |
//! | `describe`   | `peer`          | `name`, `os`, `services` (`"rdp"`, `"vnc"`) |
//! | `tunnel`     | `peer`, `service` (`"rdp"`/`"vnc"`, optional) | `host`, `port`, `service` |
//! | `probe`      | `peer`          | `rtt_ms`, `direct`                       |
//!
//! Every reply has `ok`; failures carry `error` (a sentence fit to show the user).
//! When stdin closes (the app exited), async-core exits too. A socket connection closing only
//! ends that session.

use std::sync::Arc;

use anyhow::{Context, Result};
use iroh::EndpointId;
use serde_json::{Value, json};
use tokio::{
    io::{AsyncBufReadExt, AsyncRead, AsyncWrite, AsyncWriteExt, BufReader},
    sync::{Mutex, mpsc},
    task::JoinHandle,
};

use crate::{Core, pairing, store::computer_name, tunnel::Service};

pub async fn run(core: Arc<Core>) -> Result<()> {
    session(core.clone(), tokio::io::stdin(), tokio::io::stdout()).await?;
    core.router.shutdown().await.ok();
    Ok(())
}

/// Where the background service listens for the desktop app: a socket only this user can reach.
#[cfg(unix)]
pub fn socket_path(data: &std::path::Path) -> std::path::PathBuf {
    match std::env::var_os("XDG_RUNTIME_DIR") {
        Some(dir) => std::path::PathBuf::from(dir).join("async-core.sock"),
        None => data.join("control.sock"),
    }
}

/// Serves the control protocol on a local socket, one session per connection, so the desktop app
/// can use the service that's already running instead of starting a second copy of this device.
#[cfg(unix)]
pub async fn listen(core: Arc<Core>, path: std::path::PathBuf) -> Result<()> {
    use std::os::unix::fs::PermissionsExt;
    let _ = std::fs::remove_file(&path);
    let listener = tokio::net::UnixListener::bind(&path).with_context(|| format!("listening on {}", path.display()))?;
    std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o600))?;
    loop {
        let (stream, _) = listener.accept().await?;
        let core = core.clone();
        tokio::spawn(async move {
            let (read, write) = stream.into_split();
            let _ = session(core, read, write).await;
        });
    }
}

async fn session(core: Arc<Core>, input: impl AsyncRead + Unpin, output: impl AsyncWrite + Unpin + Send + 'static) -> Result<()> {
    let (out, mut out_rx) = mpsc::unbounded_channel::<Value>();
    let writer = tokio::spawn(async move {
        let mut output = output;
        while let Some(msg) = out_rx.recv().await {
            if output.write_all(format!("{msg}\n").as_bytes()).await.is_err() || output.flush().await.is_err() {
                break;
            }
        }
    });

    out.send(json!({ "event": "ready", "node_id": core.id().to_string(), "name": computer_name() })).ok();
    let pairing: Arc<Mutex<Option<JoinHandle<()>>>> = Arc::default();

    let mut lines = BufReader::new(input).lines();
    while let Some(line) = lines.next_line().await? {
        let Ok(req) = serde_json::from_str::<Value>(&line) else { continue };
        let (core, out, pairing) = (core.clone(), out.clone(), pairing.clone());
        tokio::spawn(async move {
            let id = req.get("id").cloned().unwrap_or(Value::Null);
            let mut reply = match handle(&core, &req, &out, &pairing).await {
                Ok(v) => v,
                Err(e) => json!({ "ok": false, "error": format!("{e:#}") }),
            };
            reply["id"] = id;
            if reply.get("ok").is_none() {
                reply["ok"] = json!(true);
            }
            out.send(reply).ok();
        });
    }
    if let Some(task) = pairing.lock().await.take() {
        task.abort();
    }
    writer.abort();
    Ok(())
}

async fn handle(core: &Arc<Core>, req: &Value, out: &mpsc::UnboundedSender<Value>, pairing: &Mutex<Option<JoinHandle<()>>>) -> Result<Value> {
    let str_field = |name: &str| req.get(name).and_then(Value::as_str).with_context(|| format!("missing {name}"));
    let peer_field = || -> Result<EndpointId> { str_field("peer")?.parse().context("that isn't a device id") };

    Ok(match str_field("cmd")? {
        "info" => json!({ "node_id": core.id().to_string(), "name": computer_name(), "version": env!("CARGO_PKG_VERSION") }),
        "peers" => json!({ "peers": core.store.peers() }),
        "pair_host" => {
            let code = pairing::new_code();
            let (store, me, out, shown) = (core.store.clone(), core.id(), out.clone(), code.clone());
            let task = tokio::spawn(async move {
                match pairing::host(shown, me, store).await {
                    Ok(peer) => out.send(json!({ "event": "paired", "peer": peer })),
                    Err(e) => out.send(json!({ "event": "pair_ended", "error": format!("{e:#}") })),
                }
                .ok();
            });
            if let Some(old) = pairing.lock().await.replace(task) {
                old.abort();
            }
            json!({ "code": code, "expires_in": pairing::CODE_LIFETIME.as_secs() })
        }
        "pair_cancel" => {
            if let Some(task) = pairing.lock().await.take() {
                task.abort();
            }
            json!({})
        }
        "pair_join" => json!({ "peer": pairing::join(core.endpoint(), str_field("code")?, &core.store).await? }),
        "remove_peer" => json!({ "removed": core.store.remove_peer(str_field("peer")?)? }),
        "describe" => {
            let peer = peer_field()?;
            let info = core.tunnels.describe(peer).await?;
            // Keep the paired list's name current (it was taken at pairing time).
            if core.store.peers().iter().any(|p| p.id == peer.to_string() && p.name != info.name) {
                core.store.rename_peer(&peer.to_string(), &info.name)?;
            }
            serde_json::to_value(info)?
        }
        "tunnel" => {
            let peer = peer_field()?;
            let service = match req.get("service").and_then(Value::as_str) {
                Some(name) => Service::parse(name)?,
                None => crate::best_service(&core.tunnels.describe(peer).await?.services)?,
            };
            let addr = core.tunnels.open(peer, service).await?;
            json!({ "host": addr.ip().to_string(), "port": addr.port(), "service": service.name() })
        }
        "probe" => {
            let (rtt, direct) = core.tunnels.probe(peer_field()?).await?;
            json!({ "rtt_ms": rtt, "direct": direct })
        }
        other => anyhow::bail!("unknown command {other:?}"),
    })
}
