//! The JSON-lines protocol the Async app speaks to async-core over stdin/stdout.
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
//! | `tunnel`     | `peer`, `service` (`"rdp"`) | `host`, `port`               |
//! | `probe`      | `peer`          | `rtt_ms`, `direct`                       |
//!
//! Every reply has `ok`; failures carry `error` (a sentence fit to show the user).
//! When stdin closes (the app exited), async-core exits too.

use std::sync::Arc;

use anyhow::{Context, Result};
use iroh::EndpointId;
use serde_json::{Value, json};
use tokio::{
    io::{AsyncBufReadExt, BufReader},
    sync::{Mutex, mpsc},
    task::JoinHandle,
};

use crate::{Core, pairing, store::computer_name, tunnel::Service};

pub async fn run(core: Arc<Core>) -> Result<()> {
    let (out, mut out_rx) = mpsc::unbounded_channel::<Value>();
    tokio::spawn(async move {
        use std::io::Write;
        while let Some(msg) = out_rx.recv().await {
            let mut stdout = std::io::stdout().lock();
            let _ = writeln!(stdout, "{msg}");
            let _ = stdout.flush();
        }
    });

    out.send(json!({ "event": "ready", "node_id": core.id().to_string(), "name": computer_name() })).ok();
    let pairing: Arc<Mutex<Option<JoinHandle<()>>>> = Arc::default();

    let mut lines = BufReader::new(tokio::io::stdin()).lines();
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
    core.router.shutdown().await.ok();
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
        "tunnel" => {
            let service = Service::parse(req.get("service").and_then(Value::as_str).unwrap_or("rdp"))?;
            let addr = core.tunnels.open(peer_field()?, service).await?;
            json!({ "host": addr.ip().to_string(), "port": addr.port() })
        }
        "probe" => {
            let (rtt, direct) = core.tunnels.probe(peer_field()?).await?;
            json!({ "rtt_ms": rtt, "direct": direct })
        }
        other => anyhow::bail!("unknown command {other:?}"),
    })
}
