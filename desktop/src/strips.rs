//! Drop strips: while the left mouse button is held, a thin coloured strip lies along each edge a
//! file drag can leave by. On an edge set to move the pointer over, files dragged onto the strip
//! carry on over to the other computer with the pointer. Along the bottom of the screen, while
//! another computer shows this one in a portal (the bottom is that portal's way back), files dropped
//! on the strip go back to that computer.

use std::num::NonZeroU32;
use std::path::PathBuf;
use std::sync::Arc;

use winit::event_loop::ActiveEventLoop;
use winit::window::{Window, WindowId, WindowLevel};

use crate::config::Edge;
use crate::monitors::Monitor;

const THICKNESS: i32 = 4;

struct Strip {
    window: Arc<Window>,
    surface: softbuffer::Surface<Arc<Window>, Arc<Window>>,
    _context: softbuffer::Context<Arc<Window>>,
    /// The device this edge leads to (mouse mode), or None for the bottom's way back.
    device: Option<String>,
    color: u32,
}

#[derive(Default)]
pub struct Strips {
    strips: Vec<Strip>,
    /// Files being dragged over a strip right now.
    pub files: Vec<PathBuf>,
}

/// What a drop on a strip asks for.
pub enum Dropped {
    /// Files dropped on the bottom: send them to whoever is viewing this computer.
    ToViewer(Vec<PathBuf>),
}

impl Strips {
    pub fn showing(&self) -> bool {
        !self.strips.is_empty()
    }

    /// Lays strips along `edges`: (monitor, edge, device it leads to or None for the way back).
    pub fn show(&mut self, event_loop: &ActiveEventLoop, edges: &[(Monitor, Edge, Option<String>)], color: [u8; 3]) {
        self.hide();
        for (m, e, device) in edges {
            let (x, y, w, h) = match e {
                Edge::Top => (m.x, m.y, m.w, THICKNESS),
                Edge::Bottom => (m.x, m.bottom() - THICKNESS, m.w, THICKNESS),
                Edge::Left => (m.x, m.y, THICKNESS, m.h),
                Edge::Right => (m.right() - THICKNESS, m.y, THICKNESS, m.h),
            };
            let attrs = Window::default_attributes()
                .with_title("Async")
                .with_decorations(false)
                .with_resizable(false)
                .with_active(false)
                .with_window_level(WindowLevel::AlwaysOnTop);
            #[cfg(target_os = "macos")]
            let attrs = attrs.with_position(winit::dpi::LogicalPosition::new(x as f64, y as f64)).with_inner_size(winit::dpi::LogicalSize::new(w as f64, h as f64));
            #[cfg(not(target_os = "macos"))]
            let attrs = attrs.with_position(winit::dpi::PhysicalPosition::new(x, y)).with_inner_size(winit::dpi::PhysicalSize::new(w as u32, h as u32));
            let Ok(window) = event_loop.create_window(attrs) else { continue };
            let window = Arc::new(window);
            let Ok(context) = softbuffer::Context::new(window.clone()) else { continue };
            let Ok(surface) = softbuffer::Surface::new(&context, window.clone()) else { continue };
            let [r, g, b] = color;
            let mut strip = Strip { window, surface, _context: context, device: device.clone(), color: (r as u32) << 16 | (g as u32) << 8 | b as u32 };
            paint(&mut strip);
            self.strips.push(strip);
        }
    }

    pub fn hide(&mut self) {
        self.strips.clear();
        self.files.clear();
    }

    pub fn owns(&self, id: WindowId) -> bool {
        self.strips.iter().any(|s| s.window.id() == id)
    }

    pub fn redraw(&mut self, id: WindowId) {
        if let Some(s) = self.strips.iter_mut().find(|s| s.window.id() == id) {
            paint(s);
        }
    }

    /// Files are being dragged over a strip (one event per file).
    pub fn hovered(&mut self, path: PathBuf) {
        if !self.files.contains(&path) {
            self.files.push(path);
        }
    }

    /// A file was dropped on a strip. On a mouse-mode edge that's the drop Async causes itself once
    /// the pointer has crossed (the files already went with it); on the bottom it sends them back.
    pub fn dropped(&mut self, id: WindowId, path: PathBuf) -> Option<Dropped> {
        let s = self.strips.iter().find(|s| s.window.id() == id)?;
        if s.device.is_some() {
            return None;
        }
        Some(Dropped::ToViewer(vec![path]))
    }
}

fn paint(s: &mut Strip) {
    let size = s.window.inner_size();
    let (Some(w), Some(h)) = (NonZeroU32::new(size.width), NonZeroU32::new(size.height)) else { return };
    if s.surface.resize(w, h).is_err() {
        return;
    }
    if let Ok(mut buf) = s.surface.buffer_mut() {
        buf.fill(s.color);
        let _ = buf.present();
    }
}
