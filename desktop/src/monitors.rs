//! The monitors X11 knows about (RandR), and which of their edges can be pushed through.

use x11rb::connection::Connection;
use x11rb::protocol::randr::ConnectionExt as _;
use x11rb::protocol::xproto::ConnectionExt as _;

use crate::config::Edge;

#[derive(Debug, Clone, PartialEq)]
pub struct Monitor {
    /// The output name, e.g. "eDP-1" or "HDMI-1"; edges are remembered by it.
    pub name: String,
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
    pub primary: bool,
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

    /// Where on this monitor's edge a screen point lands, in monitor-local pixels.
    pub fn edge_point(&self, e: Edge, x: i32, y: i32) -> (f32, f32) {
        let lx = (x - self.x).clamp(0, self.w) as f32;
        let ly = (y - self.y).clamp(0, self.h) as f32;
        match e {
            Edge::Top => (lx, 0.0),
            Edge::Bottom => (lx, self.h as f32),
            Edge::Left => (0.0, ly),
            Edge::Right => (self.w as f32, ly),
        }
    }
}

pub fn list(conn: &impl Connection, root: u32) -> Vec<Monitor> {
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
            Monitor { name, x: m.x as i32, y: m.y as i32, w: m.width as i32, h: m.height as i32, primary: m.primary }
        })
        .collect()
}

/// Lists monitors over a fresh X connection (for the settings window).
pub fn list_now() -> Vec<Monitor> {
    let Ok((conn, screen)) = x11rb::connect(None) else { return Vec::new() };
    let root = conn.setup().roots[screen].root;
    list(&conn, root)
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
