//! Watches mouse movement and reports when the cursor is held against an outer monitor edge and
//! pushed through it. Movement keeps being reported after the cursor stops at an edge (XInput2 raw
//! motion on Linux, a CoreGraphics event tap's deltas on macOS), which is what makes "pushing
//! through" detectable. Edges where the mouse simply crosses onto another monitor never count.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use anyhow::Result;

use crate::config::{Edge, Settings};
use crate::monitors::{self, Monitor};

#[derive(Debug, Clone)]
pub enum Push {
    /// A push is building: 0–1 progress toward firing. (Read once the edge glow comes to Linux and macOS.)
    #[allow(dead_code)]
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

/// Turns a stream of mouse movements into pushes; the same on every platform.
struct Tracker {
    cfg: Arc<Mutex<Settings>>,
    cooldown: Cooldown,
    all: Vec<Monitor>,
    listed: u64,
    pushed: u32,
    push_edge: Option<Edge>,
    last_push: u64,
    push_start: u64,
    debug: bool,
}

impl Tracker {
    fn new(cfg: Arc<Mutex<Settings>>, cooldown: Cooldown) -> Self {
        clock();
        Self { cfg, cooldown, all: Vec::new(), listed: 0, pushed: 0, push_edge: None, last_push: 0, push_start: 0, debug: std::env::var_os("ASYNC_DEBUG").is_some() }
    }

    /// One movement of (dx, dy) device counts with the cursor at (x, y) in desktop units.
    /// `list` re-reads the monitors (every couple of seconds, so plugging one in is noticed).
    fn motion(&mut self, dx: i32, dy: i32, x: i32, y: i32, buttons: bool, list: impl FnOnce() -> Vec<Monitor>) -> Option<Push> {
        if dx == 0 && dy == 0 {
            return None;
        }
        let now = clock();
        if self.all.is_empty() || now - self.listed > 2000 {
            self.all = list();
            self.listed = now;
        }
        if self.debug {
            eprintln!("motion dx={dx} dy={dy} at {x},{y} buttons={buttons}");
        }
        // Dragging a window against the edge is the window manager's business, not a punch.
        if now < self.cooldown.load(Ordering::Relaxed) || buttons {
            self.pushed = 0;
            return None;
        }
        let m = self.all.iter().find(|m| m.contains(x, y)).cloned()?;

        // In a corner, the push goes to whichever edge it's pressing into harder.
        let mut edge = None;
        let mut amount = 0;
        for (e, d, toward) in [(Edge::Top, dy, dy < 0), (Edge::Bottom, dy, dy > 0), (Edge::Left, dx, dx < 0), (Edge::Right, dx, dx > 0)] {
            if toward && monitors::against(&self.all, &m, e, x, y) && d.unsigned_abs() > amount {
                edge = Some(e);
                amount = d.unsigned_abs();
            }
        }
        // Sliding along the edge while pushing into it neither adds nor resets.
        if edge.is_none() && self.push_edge.is_some_and(|e: Edge| if e.horizontal() { dy == 0 } else { dx == 0 }) {
            return None;
        }

        let (distance, gap, hold) = {
            let c = self.cfg.lock().unwrap();
            (c.push_distance.max(1), c.push_gap_ms, c.push_hold_ms)
        };
        if edge.is_none() || edge != self.push_edge || now - self.last_push > gap {
            self.pushed = 0;
            self.push_start = now;
        }
        self.push_edge = edge;
        self.last_push = now;
        let e = edge?;

        self.pushed += amount;
        let by_time = if hold == 0 { 1.0 } else { (now - self.push_start) as f32 / hold as f32 };
        let progress = (self.pushed as f32 / distance as f32).min(by_time).clamp(0.0, 1.0);
        if self.pushed < distance || now - self.push_start < hold {
            return Some(Push::Building { monitor: m, edge: e, x, y, progress });
        }
        self.pushed = 0;
        cool_down(&self.cooldown, 700);
        Some(Push::Fired { monitor: m, edge: e, x, y })
    }
}

#[cfg(target_os = "linux")]
pub fn spawn(cfg: Arc<Mutex<Settings>>, cooldown: Cooldown, report: impl Fn(Push) + Send + 'static) -> Result<()> {
    use anyhow::Context;
    use x11rb::connection::Connection;
    use x11rb::protocol::Event;
    use x11rb::protocol::xinput::{self, ConnectionExt as _};
    use x11rb::protocol::xproto::{ConnectionExt as _, KeyButMask};

    let (conn, screen) = x11rb::connect(None).context("can't reach the X display (Async needs an X11 session for now)")?;
    let root = conn.setup().roots[screen].root;
    conn.xinput_xi_query_version(2, 2)?.reply().context("XInput 2 isn't available")?;
    conn.xinput_xi_select_events(
        root,
        &[xinput::EventMask { deviceid: xinput::Device::ALL_MASTER.into(), mask: vec![xinput::XIEventMask::RAW_MOTION] }],
    )?;
    conn.flush()?;

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

    std::thread::Builder::new().name("edge-watcher".into()).spawn(move || {
        let mut tracker = Tracker::new(cfg, cooldown);
        while let Ok(event) = conn.wait_for_event() {
            let Event::XinputRawMotion(ev) = event else { continue };
            let (dx, dy) = raw_deltas(&ev);
            if dx == 0 && dy == 0 {
                continue;
            }
            let Ok(Ok(p)) = conn.query_pointer(root).map(|c| c.reply()) else { continue };
            let buttons = u16::from(p.mask) & u16::from(KeyButMask::BUTTON1 | KeyButMask::BUTTON2 | KeyButMask::BUTTON3) != 0;
            if let Some(push) = tracker.motion(dx, dy, p.root_x as i32, p.root_y as i32, buttons, || monitors::list(&conn, root)) {
                report(push);
            }
        }
    })?;
    Ok(())
}

/// macOS: a listen-only event tap on mouse movement. It needs the Input Monitoring permission;
/// until that's granted the watcher asks for it and keeps retrying.
#[cfg(target_os = "macos")]
pub fn spawn(cfg: Arc<Mutex<Settings>>, cooldown: Cooldown, report: impl Fn(Push) + Send + 'static) -> Result<()> {
    use core_foundation::runloop::{CFRunLoop, kCFRunLoopCommonModes};
    use core_graphics::event::{CGEventTap, CGEventTapLocation, CGEventTapOptions, CGEventTapPlacement, CGEventType, EventField};

    unsafe extern "C" {
        fn CGPreflightListenEventAccess() -> bool;
        fn CGRequestListenEventAccess() -> bool;
    }

    std::thread::Builder::new().name("edge-watcher".into()).spawn(move || {
        let tracker = std::cell::RefCell::new(Tracker::new(cfg, cooldown));
        let mut asked = false;
        loop {
            if !unsafe { CGPreflightListenEventAccess() } && !asked {
                asked = true;
                unsafe { CGRequestListenEventAccess() };
                crate::app::notify(
                    "Async",
                    "To notice you pushing through a screen edge, Async needs Input Monitoring: System Settings → Privacy & Security → Input Monitoring → turn on async-desktop.",
                );
            }
            // Only plain movement: drags (a button held) are window moves, not punches.
            let tap = CGEventTap::new(
                CGEventTapLocation::Session,
                CGEventTapPlacement::TailAppendEventTap,
                CGEventTapOptions::ListenOnly,
                vec![CGEventType::MouseMoved, CGEventType::LeftMouseDragged, CGEventType::RightMouseDragged, CGEventType::OtherMouseDragged],
                |_proxy, kind, event| {
                    let dx = event.get_integer_value_field(EventField::MOUSE_EVENT_DELTA_X) as i32;
                    let dy = event.get_integer_value_field(EventField::MOUSE_EVENT_DELTA_Y) as i32;
                    let at = event.location();
                    let buttons = !matches!(kind, CGEventType::MouseMoved);
                    if let Some(push) = tracker.borrow_mut().motion(dx, dy, at.x.floor() as i32, at.y.floor() as i32, buttons, monitors::list_now) {
                        report(push);
                    }
                    None
                },
            );
            match tap {
                Ok(tap) => unsafe {
                    let Ok(source) = tap.mach_port.create_runloop_source(0) else { return };
                    CFRunLoop::get_current().add_source(&source, kCFRunLoopCommonModes);
                    tap.enable();
                    CFRunLoop::run_current();
                    return;
                },
                // No permission yet: try again shortly.
                Err(()) => std::thread::sleep(std::time::Duration::from_secs(3)),
            }
        }
    })?;
    Ok(())
}
