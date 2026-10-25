//! Watches raw mouse movement (XInput2 raw motion, which keeps arriving after the cursor stops at
//! a screen edge) and reports when the cursor is held against an outer monitor edge and pushed
//! through it. Edges where the mouse simply crosses onto another monitor never count.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use anyhow::{Context, Result};
use x11rb::connection::Connection;
use x11rb::protocol::Event;
use x11rb::protocol::xinput::{self, ConnectionExt as _};
use x11rb::protocol::xproto::{ConnectionExt as _, KeyButMask};

use crate::config::{Edge, Settings};
use crate::monitors::{self, Monitor};

#[derive(Debug, Clone)]
pub enum Push {
    /// A push is building: 0–1 progress toward firing.
    Building { monitor: Monitor, edge: Edge, x: i32, y: i32, progress: f32 },
    Fired { monitor: Monitor, edge: Edge, x: i32, y: i32 },
}

/// Milliseconds on [`clock`] until which pushes are ignored (e.g. while an animation plays).
pub type Cooldown = Arc<AtomicU64>;

static START: std::sync::OnceLock<Instant> = std::sync::OnceLock::new();

pub fn clock() -> u64 {
    START.get_or_init(Instant::now).elapsed().as_millis() as u64
}

pub fn cool_down(c: &Cooldown, ms: u64) {
    c.store(clock() + ms, Ordering::Relaxed);
}

pub fn spawn(cfg: Arc<Mutex<Settings>>, cooldown: Cooldown, report: impl Fn(Push) + Send + 'static) -> Result<()> {
    let (conn, screen) = x11rb::connect(None).context("can't reach the X display (Async needs an X11 session for now)")?;
    let root = conn.setup().roots[screen].root;
    conn.xinput_xi_query_version(2, 2)?.reply().context("XInput 2 isn't available")?;
    conn.xinput_xi_select_events(
        root,
        &[xinput::EventMask { deviceid: xinput::Device::ALL_MASTER.into(), mask: vec![xinput::XIEventMask::RAW_MOTION] }],
    )?;
    conn.flush()?;
    clock();

    std::thread::Builder::new().name("edge-watcher".into()).spawn(move || {
        let mut all = monitors::list(&conn, root);
        let mut listed = clock();
        let (mut pushed, mut push_edge, mut last_push, mut push_start) = (0u32, None::<Edge>, 0u64, 0u64);
        let debug = std::env::var_os("ASYNC_DEBUG").is_some();

        while let Ok(event) = conn.wait_for_event() {
            let Event::XinputRawMotion(ev) = event else { continue };
            let (dx, dy) = raw_deltas(&ev);
            if dx == 0 && dy == 0 {
                continue;
            }
            let now = clock();
            if now - listed > 2000 {
                all = monitors::list(&conn, root);
                listed = now;
            }
            let Ok(Ok(p)) = conn.query_pointer(root).map(|c| c.reply()) else { continue };
            let (x, y) = (p.root_x as i32, p.root_y as i32);
            let buttons = u16::from(p.mask) & u16::from(KeyButMask::BUTTON1 | KeyButMask::BUTTON2 | KeyButMask::BUTTON3) != 0;
            if debug {
                eprintln!("raw dx={dx} dy={dy} at {x},{y} buttons={buttons}");
            }
            // Dragging a window against the edge is the window manager's business, not a punch.
            if now < cooldown.load(Ordering::Relaxed) || buttons {
                pushed = 0;
                continue;
            }
            let Some(m) = all.iter().find(|m| m.contains(x, y)).cloned() else { continue };

            // In a corner, the push goes to whichever edge it's pressing into harder.
            let mut edge = None;
            let mut amount = 0;
            for (e, d, toward) in [(Edge::Top, dy, dy < 0), (Edge::Bottom, dy, dy > 0), (Edge::Left, dx, dx < 0), (Edge::Right, dx, dx > 0)] {
                if toward && monitors::against(&all, &m, e, x, y) && d.unsigned_abs() > amount {
                    edge = Some(e);
                    amount = d.unsigned_abs();
                }
            }
            // Sliding along the edge while pushing into it neither adds nor resets.
            if edge.is_none() && push_edge.is_some_and(|e: Edge| if e.horizontal() { dy == 0 } else { dx == 0 }) {
                continue;
            }

            let (distance, gap, hold) = {
                let c = cfg.lock().unwrap();
                (c.push_distance.max(1), c.push_gap_ms, c.push_hold_ms)
            };
            if edge.is_none() || edge != push_edge || now - last_push > gap {
                pushed = 0;
                push_start = now;
            }
            push_edge = edge;
            last_push = now;
            let Some(e) = edge else { continue };

            pushed += amount;
            let by_time = if hold == 0 { 1.0 } else { (now - push_start) as f32 / hold as f32 };
            let progress = (pushed as f32 / distance as f32).min(by_time).clamp(0.0, 1.0);
            report(Push::Building { monitor: m.clone(), edge: e, x, y, progress });
            if pushed < distance || now - push_start < hold {
                continue;
            }
            pushed = 0;
            cool_down(&cooldown, 700);
            report(Push::Fired { monitor: m, edge: e, x, y });
        }
    })?;
    Ok(())
}

/// The relative x and y movement in a raw motion event (valuators 0 and 1), in device counts.
fn raw_deltas(ev: &xinput::RawMotionEvent) -> (i32, i32) {
    let (mut dx, mut dy) = (0, 0);
    let mut value = ev.axisvalues_raw.iter();
    for axis in 0..ev.valuator_mask.len() * 32 {
        if ev.valuator_mask[axis / 32] & (1 << (axis % 32)) == 0 {
            continue;
        }
        let Some(v) = value.next() else { break };
        let v = v.integral as f64 + v.frac as f64 / 4294967296.0;
        match axis {
            0 => dx = v.round() as i32,
            1 => dy = v.round() as i32,
            _ => {}
        }
    }
    (dx, dy)
}
