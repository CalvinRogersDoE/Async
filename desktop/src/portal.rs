//! The portal: a borderless fullscreen window on one monitor, showing the other computer's desktop.
//! Opening and closing grow or shrink it as a circle (X11 Shape extension) with a glowing rim.

use std::num::NonZeroU32;
use std::sync::Arc;

use anyhow::{Context, Result, anyhow};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use winit::dpi::{PhysicalPosition, PhysicalSize};
use winit::event_loop::ActiveEventLoop;
use winit::window::{Fullscreen, Window, WindowLevel};
use x11rb::connection::Connection;
use x11rb::protocol::shape::{ConnectionExt as _, SK, SO};
use x11rb::protocol::xproto::{ClipOrdering, ConnectionExt as _, Rectangle};
use x11rb::rust_connection::RustConnection;

use crate::monitors::Monitor;

const BACKGROUND: u32 = 0x060812;

pub struct Portal {
    window: Arc<Window>,
    surface: softbuffer::Surface<Arc<Window>, Arc<Window>>,
    _context: softbuffer::Context<Arc<Window>>,
    x: RustConnection,
    root: u32,
    xid: u32,
    pub monitor: Option<Monitor>,
    pub visible: bool,
    frame: Vec<u32>,
    frame_size: (u16, u16),
    /// The part of the remote desktop to show (x, y, w, h), e.g. one of its screens; None shows all.
    view: Option<(u32, u32, u32, u32)>,
}

impl Portal {
    pub fn new(event_loop: &ActiveEventLoop) -> Result<Self> {
        let attrs = Window::default_attributes()
            .with_title("Async")
            .with_decorations(false)
            .with_visible(false)
            .with_window_level(WindowLevel::AlwaysOnTop)
            .with_inner_size(PhysicalSize::new(640, 480));
        let window = Arc::new(event_loop.create_window(attrs)?);
        let context = softbuffer::Context::new(window.clone()).map_err(|e| anyhow!("{e}"))?;
        let surface = softbuffer::Surface::new(&context, window.clone()).map_err(|e| anyhow!("{e}"))?;
        let xid = match window.window_handle()?.as_raw() {
            RawWindowHandle::Xlib(h) => h.window as u32,
            RawWindowHandle::Xcb(h) => h.window.get(),
            _ => return Err(anyhow!("Async needs an X11 session for now (Wayland comes later)")),
        };
        let (x, screen) = x11rb::connect(None).context("can't reach the X display")?;
        let root = x.setup().roots[screen].root;
        Ok(Self { window, surface, _context: context, x, root, xid, monitor: None, visible: false, frame: Vec::new(), frame_size: (0, 0), view: None })
    }

    pub fn window(&self) -> &Window {
        &self.window
    }

    /// Covers `m`, still fully cut away (invisible) until [`Portal::set_circle`] opens it up.
    pub fn show_on(&mut self, m: &Monitor) {
        self.set_circle(0.0, 0.0, 0.0);
        let handle = self.window.available_monitors().find(|h| {
            let p = h.position();
            p.x == m.x && p.y == m.y
        });
        self.window.set_outer_position(PhysicalPosition::new(m.x, m.y));
        let _ = self.window.request_inner_size(PhysicalSize::new(m.w as u32, m.h as u32));
        self.window.set_fullscreen(Some(Fullscreen::Borderless(handle)));
        self.window.set_visible(true);
        self.window.focus_window();
        self.monitor = Some(m.clone());
        self.visible = true;
    }

    pub fn hide(&mut self) {
        self.window.set_visible(false);
        self.visible = false;
    }

    /// Cuts the window to a circle of radius `r` around (cx, cy), in window pixels.
    pub fn set_circle(&self, cx: f32, cy: f32, r: f32) {
        let (w, h) = self.size();
        let mut rects = Vec::new();
        let r = r.max(0.0);
        let mut dy = -r;
        while dy < r {
            let half = (r * r - dy * dy).max(0.0).sqrt();
            let (y0, x0, x1) = ((cy + dy) as i32, (cx - half) as i32, (cx + half) as i32);
            let (x0, x1) = (x0.max(0), x1.min(w as i32));
            if y0 >= 0 && y0 < h as i32 && x1 > x0 {
                rects.push(Rectangle { x: x0 as i16, y: y0 as i16, width: (x1 - x0) as u16, height: 2 });
            }
            dy += 2.0;
        }
        let _ = self.x.shape_rectangles(SO::SET, SK::BOUNDING, ClipOrdering::UNSORTED, self.xid, 0, 0, &rects);
        let _ = self.x.flush();
    }

    /// Back to a plain rectangle.
    pub fn clear_shape(&self) {
        let _ = self.x.shape_mask(SO::SET, SK::BOUNDING, self.xid, 0, 0, x11rb::NONE);
        let _ = self.x.flush();
    }

    pub fn warp_pointer(&self, x: i32, y: i32) {
        let _ = self.x.warp_pointer(x11rb::NONE, self.root, 0, 0, 0, 0, x as i16, y as i16);
        let _ = self.x.flush();
    }

    pub fn size(&self) -> (u32, u32) {
        match &self.monitor {
            Some(m) => (m.w as u32, m.h as u32),
            None => {
                let s = self.window.inner_size();
                (s.width, s.height)
            }
        }
    }

    pub fn set_frame(&mut self, pixels: Vec<u32>, width: u16, height: u16) {
        self.frame = pixels;
        self.frame_size = (width, height);
    }

    pub fn clear_frame(&mut self) {
        self.frame.clear();
        self.frame_size = (0, 0);
    }

    pub fn set_view(&mut self, view: Option<(u32, u32, u32, u32)>) {
        self.view = view;
    }

    /// The shown part of the remote desktop, clamped to the frame.
    fn source(&self) -> Option<(u32, u32, u32, u32)> {
        let (fw, fh) = (self.frame_size.0 as u32, self.frame_size.1 as u32);
        if fw == 0 || fh == 0 || self.frame.len() != (fw * fh) as usize {
            return None;
        }
        let (x, y, w, h) = self.view.unwrap_or((0, 0, fw, fh));
        let (x, y) = (x.min(fw - 1), y.min(fh - 1));
        Some((x, y, w.min(fw - x).max(1), h.min(fh - y).max(1)))
    }

    /// Where the picture sits in the window (x, y, w, h), keeping its shape.
    fn placement(&self, w: u32, h: u32) -> Option<(f64, f64, f64, f64, (u32, u32, u32, u32))> {
        let src = self.source()?;
        let k = (w as f64 / src.2 as f64).min(h as f64 / src.3 as f64);
        let (dw, dh) = (src.2 as f64 * k, src.3 as f64 * k);
        Some(((w as f64 - dw) / 2.0, (h as f64 - dh) / 2.0, dw, dh, src))
    }

    /// Maps a window position to the remote desktop's pixels, or None over the letterbox bars.
    pub fn to_desktop(&self, x: f64, y: f64) -> Option<(f64, f64)> {
        let size = self.window.inner_size();
        let (ox, oy, dw, dh, (sx, sy, sw, sh)) = self.placement(size.width, size.height)?;
        let (u, v) = ((x - ox) / dw, (y - oy) / dh);
        if !(0.0..=1.0).contains(&u) || !(0.0..=1.0).contains(&v) {
            return None;
        }
        Some((sx as f64 + u * (sw as f64 - 1.0), sy as f64 + v * (sh as f64 - 1.0)))
    }

    /// Draws the remote desktop (or a dark placeholder while it connects), plus a rim when `rim` is set:
    /// (center x, center y, radius, colour).
    pub fn draw(&mut self, rim: Option<(f32, f32, f32, [u8; 3])>) {
        let size = self.window.inner_size();
        let (w, h) = (size.width.max(1), size.height.max(1));
        let (Some(nw), Some(nh)) = (NonZeroU32::new(w), NonZeroU32::new(h)) else { return };
        if self.surface.resize(nw, nh).is_err() {
            return;
        }
        let placement = self.placement(w, h);
        let fw = self.frame_size.0 as usize;
        let Ok(mut buf) = self.surface.buffer_mut() else { return };
        match placement {
            Some((ox, oy, dw, dh, (sx, sy, sw, sh))) if dw.round() as u32 == sw && dh.round() as u32 == sh && ox == 0.0 && oy == 0.0 => {
                // 1:1, straight copy.
                for y in 0..h as usize {
                    let src = (sy as usize + y) * fw + sx as usize;
                    buf[y * w as usize..(y + 1) * w as usize].copy_from_slice(&self.frame[src..src + w as usize]);
                }
            }
            Some((ox, oy, dw, dh, src)) => {
                buf.fill(BACKGROUND);
                scale_into(&mut buf, w, &self.frame, fw, src, (ox, oy, dw, dh));
            }
            None => buf.fill(BACKGROUND),
        }
        if let Some((cx, cy, r, color)) = rim {
            paint_rim(&mut buf, w, h, cx, cy, r, color);
        }
        let _ = buf.present();
    }
}

/// Bilinear scale of `src` (x, y, w, h) of `frame` into the `dst` rectangle of the window buffer.
fn scale_into(buf: &mut [u32], bw: u32, frame: &[u32], fw: usize, src: (u32, u32, u32, u32), dst: (f64, f64, f64, f64)) {
    let (sx, sy, sw, sh) = (src.0 as f64, src.1 as f64, src.2 as f64, src.3 as f64);
    let (ox, oy, dw, dh) = dst;
    let (x0, y0) = (ox.round().max(0.0) as usize, oy.round().max(0.0) as usize);
    let (x1, y1) = (((ox + dw).round() as usize).min(bw as usize), ((oy + dh).round() as usize).min(buf.len() / bw as usize));
    let (max_x, max_y) = (sx + sw - 1.0, sy + sh - 1.0);
    // Source column and weight for each output column, worked out once per frame.
    let cols: Vec<(usize, usize, u32)> = (x0..x1)
        .map(|x| {
            let fx = (sx + (x as f64 + 0.5 - ox) * sw / dw - 0.5).clamp(sx, max_x);
            let c = fx.floor();
            (c as usize, (c + 1.0).min(max_x) as usize, ((fx - c) * 256.0) as u32)
        })
        .collect();
    for y in y0..y1 {
        let fy = (sy + (y as f64 + 0.5 - oy) * sh / dh - 0.5).clamp(sy, max_y);
        let r = fy.floor();
        let (r0, r1, wy) = (r as usize * fw, (r + 1.0).min(max_y) as usize * fw, ((fy - r) * 256.0) as u32);
        let row = &mut buf[y * bw as usize..];
        for (i, &(c0, c1, wx)) in cols.iter().enumerate() {
            row[x0 + i] = blend(blend(frame[r0 + c0], frame[r0 + c1], wx), blend(frame[r1 + c0], frame[r1 + c1], wx), wy);
        }
    }
}

/// Mixes two 0RGB pixels, `t` out of 256 toward `b`.
fn blend(a: u32, b: u32, t: u32) -> u32 {
    let rb = ((a & 0xFF00FF) * (256 - t) + (b & 0xFF00FF) * t) >> 8 & 0xFF00FF;
    let g = ((a & 0x00FF00) * (256 - t) + (b & 0x00FF00) * t) >> 8 & 0x00FF00;
    rb | g
}

/// A glowing ring just inside radius `r`, fading inwards.
fn paint_rim(buf: &mut [u32], w: u32, h: u32, cx: f32, cy: f32, r: f32, color: [u8; 3]) {
    const WIDTH: f32 = 16.0;
    if r < 2.0 {
        return;
    }
    let inner = (r - WIDTH).max(0.0);
    let y0 = ((cy - r).floor() as i32).max(0);
    let y1 = ((cy + r).ceil() as i32).min(h as i32 - 1);
    for y in y0..=y1 {
        let dy = y as f32 + 0.5 - cy;
        let outer_half = (r * r - dy * dy).max(0.0).sqrt();
        let inner_half = (inner * inner - dy * dy).max(0.0).sqrt();
        // Two spans per row: left and right parts of the ring (or one, near the top and bottom).
        for (a, b) in [(cx - outer_half, cx - inner_half), (cx + inner_half, cx + outer_half)] {
            let (xa, xb) = ((a.floor() as i32).max(0), (b.ceil() as i32).min(w as i32 - 1));
            for x in xa..=xb {
                let dx = x as f32 + 0.5 - cx;
                let d = (dx * dx + dy * dy).sqrt();
                if d > r || d < inner {
                    continue;
                }
                // Bright at the edge, fading inwards; white-hot right at the rim.
                let t = 1.0 - (r - d) / WIDTH;
                let k = t * t;
                let hot = (t - 0.8).max(0.0) * 5.0;
                let i = (y as u32 * w + x as u32) as usize;
                let px = buf[i];
                let mix = |shift: u32, c: u8| {
                    let base = ((px >> shift) & 0xFF) as f32;
                    let tint = c as f32 + (255.0 - c as f32) * hot;
                    ((base + (tint - base) * k).clamp(0.0, 255.0) as u32) << shift
                };
                buf[i] = mix(16, color[0]) | mix(8, color[1]) | mix(0, color[2]);
            }
        }
    }
}
