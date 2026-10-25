//! The monitors this computer has (RandR on Linux, CoreGraphics on macOS), and which of their edges
//! can be pushed through.
//!
//! Positions are in the desktop's own units: pixels on Linux, points on macOS (where `scale` says
//! how many pixels each point has).

use crate::config::Edge;

#[derive(Debug, Clone, PartialEq)]
pub struct Monitor {
    /// A stable name edges are remembered by: the output name on Linux ("eDP-1"), "display-<id>" on macOS.
    pub name: String,
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
    pub primary: bool,
    /// Pixels per desktop unit (Retina is 2).
    pub scale: f64,
}

impl Monitor {
    pub fn contains(&self, x: i32, y: i32) -> bool {
        x >= self.x && x < self.x + self.w && y >= self.y && y < self.y + self.h
    }
    pub fn right(&self) -> i32 {
        self.x + self.w
    }
    pub fn bottom(&self) -> i32 {
        self.y + self.h
    }
    /// The monitor's size in pixels.
    pub fn pixels(&self) -> (u32, u32) {
        ((self.w as f64 * self.scale).round() as u32, (self.h as f64 * self.scale).round() as u32)
    }

    /// Where on this monitor's edge a desktop point lands, in the monitor's pixels.
    pub fn edge_point(&self, e: Edge, x: i32, y: i32) -> (f32, f32) {
        let s = self.scale as f32;
        let lx = (x - self.x).clamp(0, self.w) as f32 * s;
        let ly = (y - self.y).clamp(0, self.h) as f32 * s;
        let (w, h) = (self.w as f32 * s, self.h as f32 * s);
        match e {
            Edge::Top => (lx, 0.0),
            Edge::Bottom => (lx, h),
            Edge::Left => (0.0, ly),
            Edge::Right => (w, ly),
        }
    }
}

#[cfg(target_os = "linux")]
pub fn list(conn: &impl x11rb::connection::Connection, root: u32) -> Vec<Monitor> {
    use x11rb::protocol::randr::ConnectionExt as _;
    use x11rb::protocol::xproto::ConnectionExt as _;
    let Ok(reply) = conn.randr_get_monitors(root, true).map_err(anyhow::Error::from).and_then(|c| Ok(c.reply()?)) else {
        return Vec::new();
    };
    reply
        .monitors
        .iter()
        .map(|m| {
            let name = conn
                .get_atom_name(m.name)
                .ok()
                .and_then(|c| c.reply().ok())
                .map(|r| String::from_utf8_lossy(&r.name).into_owned())
                .unwrap_or_else(|| format!("monitor-{}", m.x));
            Monitor { name, x: m.x as i32, y: m.y as i32, w: m.width as i32, h: m.height as i32, primary: m.primary, scale: 1.0 }
        })
        .collect()
}

/// Lists monitors right now (for the settings window and the tray's "Punch in here").
#[cfg(target_os = "linux")]
pub fn list_now() -> Vec<Monitor> {
    use x11rb::connection::Connection as _;
    let Ok((conn, screen)) = x11rb::connect(None) else { return Vec::new() };
    let root = conn.setup().roots[screen].root;
    list(&conn, root)
}

#[cfg(target_os = "macos")]
pub fn list_now() -> Vec<Monitor> {
    use core_graphics::display::CGDisplay;
    let main = CGDisplay::main().id;
    CGDisplay::active_displays()
        .unwrap_or_default()
        .into_iter()
        .map(|id| {
            let d = CGDisplay::new(id);
            let b = d.bounds();
            let scale = if b.size.width > 0.0 { d.pixels_wide() as f64 / b.size.width } else { 1.0 };
            Monitor {
                name: format!("display-{id}"),
                x: b.origin.x.round() as i32,
                y: b.origin.y.round() as i32,
                w: b.size.width.round() as i32,
                h: b.size.height.round() as i32,
                primary: id == main,
                scale,
            }
        })
        .collect()
}

fn screen_at(all: &[Monitor], x: i32, y: i32) -> bool {
    all.iter().any(|m| m.contains(x, y))
}

/// Whether the cursor at (x, y) on `m` is against `e` with no other monitor beyond it.
pub fn against(all: &[Monitor], m: &Monitor, e: Edge, x: i32, y: i32) -> bool {
    match e {
        Edge::Top => y <= m.y && !screen_at(all, x, m.y - 1),
        Edge::Bottom => y >= m.bottom() - 1 && !screen_at(all, x, m.bottom()),
        Edge::Left => x <= m.x && !screen_at(all, m.x - 1, y),
        Edge::Right => x >= m.right() - 1 && !screen_at(all, m.right(), y),
    }
}

/// Whether part of this monitor edge is free (not joined to another monitor).
pub fn is_free(all: &[Monitor], m: &Monitor, e: Edge) -> bool {
    (0..24).any(|i| {
        let x = m.x + ((i as f32 + 0.5) * m.w as f32 / 24.0) as i32;
        let y = m.y + ((i as f32 + 0.5) * m.h as f32 / 24.0) as i32;
        match e {
            Edge::Top => !screen_at(all, x, m.y - 1),
            Edge::Bottom => !screen_at(all, x, m.bottom()),
            Edge::Left => !screen_at(all, m.x - 1, y),
            Edge::Right => !screen_at(all, m.right(), y),
        }
    })
}
