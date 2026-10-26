//! Mouse mode, the receiving side: a paired computer's pointer and keyboard cross over to this one.
//!
//! The other computer's Async connects to the `input` service, which async-core hands to this app
//! on 127.0.0.1:24850. Messages are small binary frames, a type byte then little-endian fields:
//!
//! | type | name      | fields                       | direction        |
//! |------|-----------|------------------------------|------------------|
//! | 1    | Enter     | edge u8, pos f32 (0–1)       | to this computer |
//! | 2    | Move      | dx i16, dy i16 (pixels)      | to this computer |
//! | 3    | Button    | button u8, down u8           | to this computer |
//! | 4    | Wheel     | dy i16, dx i16 (1/120 notch) | to this computer |
//! | 5    | Key       | code u16 (Linux evdev), down u8 | to this computer |
//! | 6    | Leave     | edge u8, pos f32             | back to the other one |
//! | 7    | Clipboard | len u32, UTF-8 text          | both ways        |
//! | 8    | Release   |                              | to this computer |
//!
//! Edges are 0 top, 1 bottom, 2 left, 3 right, seen from this computer. Buttons are 1 left,
//! 2 middle, 3 right, 4 back, 5 forward. The pointer appears on the edge it came in through and goes
//! back when it's pushed out through that same edge.

use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};

use anyhow::Result;

use crate::edges::{Cooldown, cool_down};
use crate::monitors::{self, Monitor};

pub const PORT: u16 = 24850;
/// How far past the edge the pointer has to be pushed to go back (desktop units).
const LEAVE_PUSH: i32 = 12;

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Side {
    Top,
    Bottom,
    Left,
    Right,
}

impl Side {
    fn from_byte(b: u8) -> Self {
        match b {
            0 => Self::Top,
            1 => Self::Bottom,
            2 => Self::Left,
            _ => Self::Right,
        }
    }
    fn byte(self) -> u8 {
        self as u8
    }
}

/// Listens for the input service in the background.
pub fn spawn(cooldown: Cooldown) -> Result<()> {
    let listener = TcpListener::bind(("127.0.0.1", PORT))?;
    std::thread::spawn(move || {
        for stream in listener.incoming().flatten() {
            // async-core also connects just to see whether the service is up; that ends at once.
            let cooldown = cooldown.clone();
            std::thread::spawn(move || {
                if let Err(e) = serve(stream, cooldown) {
                    eprintln!("mouse mode ended: {e:#}");
                }
            });
        }
    });
    Ok(())
}

fn serve(stream: TcpStream, cooldown: Cooldown) -> Result<()> {
    stream.set_nodelay(true).ok();
    let mut out = stream.try_clone()?;
    let mut input = std::io::BufReader::new(stream);
    let mut inject = platform::Injector::new()?;
    let mut s = Session { all: monitors::list_now(), x: 0.0, y: 0.0, entry: None, pushed: 0 };

    loop {
        let mut kind = [0u8; 1];
        if input.read_exact(&mut kind).is_err() {
            inject.release_all();
            return Ok(());
        }
        // While the other computer drives this one, this computer's own edges don't punch out.
        cool_down(&cooldown, 600);
        match kind[0] {
            1 => {
                let edge = Side::from_byte(read_u8(&mut input)?);
                let pos = read_f32(&mut input)?.clamp(0.0, 1.0);
                s.all = monitors::list_now();
                let (x, y) = s.enter_point(edge, pos);
                (s.x, s.y, s.entry, s.pushed) = (x, y, Some(edge), 0);
                inject.move_to(x, y);
            }
            2 => {
                let dx = read_i16(&mut input)? as f64;
                let dy = read_i16(&mut input)? as f64;
                if let Some(leave) = s.apply(dx, dy) {
                    inject.release_all();
                    let mut msg = vec![6, leave.0.byte()];
                    msg.extend_from_slice(&leave.1.to_le_bytes());
                    out.write_all(&msg)?;
                    if let Some(text) = platform::clipboard_get() {
                        send_clipboard(&mut out, &text)?;
                    }
                    s.entry = None;
                    continue;
                }
                inject.move_to(s.x, s.y);
            }
            3 => {
                let button = read_u8(&mut input)?;
                let down = read_u8(&mut input)? != 0;
                inject.button(button, down, s.x, s.y);
            }
            4 => {
                let dy = read_i16(&mut input)?;
                let dx = read_i16(&mut input)?;
                inject.wheel(dy, dx, s.x, s.y);
            }
            5 => {
                let code = read_u16(&mut input)?;
                let down = read_u8(&mut input)? != 0;
                inject.key(code, down);
            }
            7 => {
                let len = read_u32(&mut input)? as usize;
                let mut text = vec![0u8; len.min(16 << 20)];
                input.read_exact(&mut text)?;
                platform::clipboard_set(&String::from_utf8_lossy(&text));
            }
            8 => inject.release_all(),
            other => anyhow::bail!("unknown mouse-mode message {other}"),
        }
    }
}

fn send_clipboard(out: &mut TcpStream, text: &str) -> Result<()> {
    let mut msg = vec![7];
    msg.extend_from_slice(&(text.len() as u32).to_le_bytes());
    msg.extend_from_slice(text.as_bytes());
    out.write_all(&msg)?;
    Ok(())
}

/// Where the crossed-over pointer is, and whether it's being pushed back out.
struct Session {
    all: Vec<Monitor>,
    x: f64,
    y: f64,
    entry: Option<Side>,
    pushed: i32,
}

impl Session {
    /// The whole desktop's bounds (left, top, right, bottom).
    fn bounds(&self) -> (f64, f64, f64, f64) {
        if self.all.is_empty() {
            return (0.0, 0.0, 1920.0, 1080.0);
        }
        let l = self.all.iter().map(|m| m.x).min().unwrap() as f64;
        let t = self.all.iter().map(|m| m.y).min().unwrap() as f64;
        let r = self.all.iter().map(|m| m.right()).max().unwrap() as f64;
        let b = self.all.iter().map(|m| m.bottom()).max().unwrap() as f64;
        (l, t, r, b)
    }

    fn enter_point(&self, edge: Side, pos: f32) -> (f64, f64) {
        let (l, t, r, b) = self.bounds();
        let (x, y) = match edge {
            Side::Top => (l + (r - l) * pos as f64, t + 1.0),
            Side::Bottom => (l + (r - l) * pos as f64, b - 2.0),
            Side::Left => (l + 1.0, t + (b - t) * pos as f64),
            Side::Right => (r - 2.0, t + (b - t) * pos as f64),
        };
        self.clamp(x, y)
    }

    /// Keeps a point on some monitor (the nearest one, if it fell into a gap between them).
    fn clamp(&self, x: f64, y: f64) -> (f64, f64) {
        let mut best = (x, y);
        let mut best_d = f64::MAX;
        for m in &self.all {
            let cx = x.clamp(m.x as f64, (m.right() - 1) as f64);
            let cy = y.clamp(m.y as f64, (m.bottom() - 1) as f64);
            let d = (cx - x).powi(2) + (cy - y).powi(2);
            if d < best_d {
                best_d = d;
                best = (cx, cy);
            }
        }
        best
    }

    /// Moves the pointer; returns where it left if it was pushed out through the edge it came in by.
    fn apply(&mut self, dx: f64, dy: f64) -> Option<(Side, f32)> {
        let (nx, ny) = (self.x + dx, self.y + dy);
        let (cx, cy) = self.clamp(nx, ny);
        let (l, t, r, b) = self.bounds();
        if let Some(entry) = self.entry {
            // Only the desktop's outer edge counts, not the edge between two monitors.
            let over = match entry {
                Side::Left if cx <= l + 0.5 && nx < cx => (cx - nx) as i32,
                Side::Right if cx >= r - 1.5 && nx > cx => (nx - cx) as i32,
                Side::Top if cy <= t + 0.5 && ny < cy => (cy - ny) as i32,
                Side::Bottom if cy >= b - 1.5 && ny > cy => (ny - cy) as i32,
                _ => 0,
            };
            self.pushed = if over > 0 { self.pushed + over } else { 0 };
            if self.pushed >= LEAVE_PUSH {
                let pos = match entry {
                    Side::Left | Side::Right => (cy - t) / (b - t),
                    Side::Top | Side::Bottom => (cx - l) / (r - l),
                };
                return Some((entry, pos.clamp(0.0, 1.0) as f32));
            }
        }
        (self.x, self.y) = (cx, cy);
        None
    }
}

fn read_u8(r: &mut impl Read) -> Result<u8> {
    let mut b = [0u8; 1];
    r.read_exact(&mut b)?;
    Ok(b[0])
}
fn read_u16(r: &mut impl Read) -> Result<u16> {
    let mut b = [0u8; 2];
    r.read_exact(&mut b)?;
    Ok(u16::from_le_bytes(b))
}
fn read_i16(r: &mut impl Read) -> Result<i16> {
    Ok(read_u16(r)? as i16)
}
fn read_u32(r: &mut impl Read) -> Result<u32> {
    let mut b = [0u8; 4];
    r.read_exact(&mut b)?;
    Ok(u32::from_le_bytes(b))
}
fn read_f32(r: &mut impl Read) -> Result<f32> {
    Ok(f32::from_bits(read_u32(r)?))
}

// ---------- elsewhere: prints what it would do (for testing the protocol on a development PC) ----------

#[cfg(not(any(target_os = "linux", target_os = "macos")))]
mod platform {
    pub struct Injector;
    impl Injector {
        pub fn new() -> anyhow::Result<Self> {
            Ok(Self)
        }
        pub fn move_to(&mut self, x: f64, y: f64) {
            println!("move {x:.0} {y:.0}");
        }
        pub fn button(&mut self, button: u8, down: bool, x: f64, y: f64) {
            println!("button {button} {} at {x:.0} {y:.0}", if down { "down" } else { "up" });
        }
        pub fn wheel(&mut self, dy: i16, dx: i16, _x: f64, _y: f64) {
            println!("wheel {dy} {dx}");
        }
        pub fn key(&mut self, code: u16, down: bool) {
            println!("key {code} {}", if down { "down" } else { "up" });
        }
        pub fn release_all(&mut self) {
            println!("release all");
        }
    }
    pub fn clipboard_get() -> Option<String> {
        Some("clipboard from the other side".into())
    }
    pub fn clipboard_set(text: &str) {
        println!("clipboard {text:?}");
    }
}

// ---------- Linux (X11): XTest ----------

#[cfg(target_os = "linux")]
mod platform {
    use anyhow::Result;
    use x11rb::connection::Connection;
    use x11rb::protocol::xproto::ConnectionExt as _;
    use x11rb::protocol::xtest::ConnectionExt as _;
    use x11rb::rust_connection::RustConnection;

    const KEY_PRESS: u8 = 2;
    const KEY_RELEASE: u8 = 3;
    const BUTTON_PRESS: u8 = 4;
    const BUTTON_RELEASE: u8 = 5;
    const MOTION: u8 = 6;

    pub struct Injector {
        conn: RustConnection,
        root: u32,
        keys: Vec<u16>,
        buttons: Vec<u8>,
    }

    impl Injector {
        pub fn new() -> Result<Self> {
            let (conn, screen) = x11rb::connect(None)?;
            let root = conn.setup().roots[screen].root;
            Ok(Self { conn, root, keys: Vec::new(), buttons: Vec::new() })
        }

        fn fake(&self, kind: u8, detail: u8, x: i16, y: i16) {
            let _ = self.conn.xtest_fake_input(kind, detail, 0, self.root, x, y, 0);
            let _ = self.conn.flush();
        }

        pub fn move_to(&mut self, x: f64, y: f64) {
            self.fake(MOTION, 0, x.round() as i16, y.round() as i16);
        }

        pub fn button(&mut self, button: u8, down: bool, _x: f64, _y: f64) {
            // X numbers: 1 left, 2 middle, 3 right, 8 back, 9 forward.
            let b = match button {
                1 => 1,
                2 => 2,
                3 => 3,
                4 => 8,
                5 => 9,
                _ => return,
            };
            self.fake(if down { BUTTON_PRESS } else { BUTTON_RELEASE }, b, 0, 0);
            self.buttons.retain(|&x| x != b);
            if down {
                self.buttons.push(b);
            }
        }

        pub fn wheel(&mut self, dy: i16, dx: i16, _x: f64, _y: f64) {
            // A click of buttons 4/5 (up/down) and 6/7 (left/right) per notch.
            let click = |b: u8, n: i32| {
                for _ in 0..n.unsigned_abs().max(1).min(20) {
                    self.fake(BUTTON_PRESS, b, 0, 0);
                    self.fake(BUTTON_RELEASE, b, 0, 0);
                }
            };
            if dy != 0 {
                click(if dy > 0 { 4 } else { 5 }, (dy as i32) / 120);
            }
            if dx != 0 {
                click(if dx > 0 { 7 } else { 6 }, (dx as i32) / 120);
            }
        }

        pub fn key(&mut self, code: u16, down: bool) {
            // X keycodes are evdev codes plus 8.
            let Ok(k) = u8::try_from(code + 8) else { return };
            self.fake(if down { KEY_PRESS } else { KEY_RELEASE }, k, 0, 0);
            self.keys.retain(|&x| x != code);
            if down {
                self.keys.push(code);
            }
        }

        pub fn release_all(&mut self) {
            for code in std::mem::take(&mut self.keys) {
                self.key(code, false);
            }
            self.keys.clear();
            for b in std::mem::take(&mut self.buttons) {
                self.fake(BUTTON_RELEASE, b, 0, 0);
            }
        }
    }

    impl Drop for Injector {
        fn drop(&mut self) {
            self.release_all();
            let _ = self.conn.ungrab_pointer(x11rb::CURRENT_TIME);
        }
    }

    /// The clipboard through xclip or xsel, whichever is installed (X11 clipboards need a running owner).
    pub fn clipboard_get() -> Option<String> {
        for (cmd, args) in [("xclip", &["-selection", "clipboard", "-o"][..]), ("xsel", &["--clipboard", "--output"][..])] {
            if let Ok(out) = std::process::Command::new(cmd).args(args).output() {
                if out.status.success() {
                    return String::from_utf8(out.stdout).ok().filter(|s| !s.is_empty());
                }
            }
        }
        None
    }

    pub fn clipboard_set(text: &str) {
        use std::io::Write;
        for (cmd, args) in [("xclip", &["-selection", "clipboard"][..]), ("xsel", &["--clipboard", "--input"][..])] {
            if let Ok(mut child) = std::process::Command::new(cmd).args(args).stdin(std::process::Stdio::piped()).spawn() {
                if let Some(mut stdin) = child.stdin.take() {
                    let _ = stdin.write_all(text.as_bytes());
                }
                let _ = child.wait();
                return;
            }
        }
    }
}

// ---------- macOS: CoreGraphics events ----------

#[cfg(target_os = "macos")]
mod platform {
    use anyhow::{Result, anyhow};
    use core_graphics::event::{CGEvent, CGEventFlags, CGEventTapLocation, CGEventType, CGKeyCode, CGMouseButton, EventField, ScrollEventUnit};
    use core_graphics::event_source::{CGEventSource, CGEventSourceStateID};
    use core_graphics::geometry::CGPoint;

    #[link(name = "ApplicationServices", kind = "framework")]
    unsafe extern "C" {
        fn AXIsProcessTrustedWithOptions(options: core_foundation::dictionary::CFDictionaryRef) -> bool;
        static kAXTrustedCheckOptionPrompt: core_foundation::string::CFStringRef;
    }

    /// Whether macOS lets this app move the pointer and type (Accessibility); asks once if not.
    fn trusted() -> bool {
        use core_foundation::base::TCFType;
        use core_foundation::boolean::CFBoolean;
        use core_foundation::dictionary::CFDictionary;
        use core_foundation::string::CFString;
        let key = unsafe { CFString::wrap_under_get_rule(kAXTrustedCheckOptionPrompt) };
        let options = CFDictionary::from_CFType_pairs(&[(key, CFBoolean::true_value())]);
        unsafe { AXIsProcessTrustedWithOptions(options.as_concrete_TypeRef()) }
    }

    pub struct Injector {
        source: CGEventSource,
        flags: CGEventFlags,
        held: [bool; 6],
        keys: Vec<u16>,
        last_click: (u8, std::time::Instant, f64, f64),
        clicks: i64,
    }

    impl Injector {
        pub fn new() -> Result<Self> {
            if !trusted() {
                crate::app::notify(
                    "Async",
                    "To let your other computer move the pointer here, allow async-desktop in System Settings > Privacy & Security > Accessibility.",
                );
            }
            let source = CGEventSource::new(CGEventSourceStateID::HIDSystemState).map_err(|_| anyhow!("no event source"))?;
            Ok(Self { source, flags: CGEventFlags::CGEventFlagNull, held: [false; 6], keys: Vec::new(), last_click: (0, std::time::Instant::now(), 0.0, 0.0), clicks: 0 })
        }

        fn post(&self, e: CGEvent) {
            e.set_flags(self.flags);
            e.post(CGEventTapLocation::HID);
        }

        pub fn move_to(&mut self, x: f64, y: f64) {
            // A move with a button held is a drag, which is what lets windows and files be dragged.
            let (kind, button) = if self.held[1] {
                (CGEventType::LeftMouseDragged, CGMouseButton::Left)
            } else if self.held[3] {
                (CGEventType::RightMouseDragged, CGMouseButton::Right)
            } else if self.held[2] || self.held[4] || self.held[5] {
                (CGEventType::OtherMouseDragged, CGMouseButton::Center)
            } else {
                (CGEventType::MouseMoved, CGMouseButton::Left)
            };
            if let Ok(e) = CGEvent::new_mouse_event(self.source.clone(), kind, CGPoint::new(x, y), button) {
                self.post(e);
            }
        }

        pub fn button(&mut self, button: u8, down: bool, x: f64, y: f64) {
            let (kind, cg) = match (button, down) {
                (1, true) => (CGEventType::LeftMouseDown, CGMouseButton::Left),
                (1, false) => (CGEventType::LeftMouseUp, CGMouseButton::Left),
                (3, true) => (CGEventType::RightMouseDown, CGMouseButton::Right),
                (3, false) => (CGEventType::RightMouseUp, CGMouseButton::Right),
                (2 | 4 | 5, true) => (CGEventType::OtherMouseDown, CGMouseButton::Center),
                (2 | 4 | 5, false) => (CGEventType::OtherMouseUp, CGMouseButton::Center),
                _ => return,
            };
            if let Some(h) = self.held.get_mut(button as usize) {
                *h = down;
            }
            // Double and triple clicks: macOS counts them only if the event says which click it is.
            if down {
                let (b, at, px, py) = self.last_click;
                let near = (px - x).abs() < 5.0 && (py - y).abs() < 5.0;
                self.clicks = if b == button && near && at.elapsed().as_millis() < 500 { self.clicks + 1 } else { 1 };
                self.last_click = (button, std::time::Instant::now(), x, y);
            }
            if let Ok(e) = CGEvent::new_mouse_event(self.source.clone(), kind, CGPoint::new(x, y), cg) {
                e.set_integer_value_field(EventField::MOUSE_EVENT_CLICK_STATE, self.clicks.max(1));
                // Middle is button 2, back 3, forward 4 in macOS numbering.
                let number = match button {
                    2 => 2,
                    4 => 3,
                    5 => 4,
                    _ => 0,
                };
                if number != 0 {
                    e.set_integer_value_field(EventField::MOUSE_EVENT_BUTTON_NUMBER, number);
                }
                self.post(e);
            }
        }

        pub fn wheel(&mut self, dy: i16, dx: i16, _x: f64, _y: f64) {
            // Lines, three per notch like a Mac mouse wheel; fractions of a notch (smooth wheels) round up.
            let lines = |d: i16| {
                let n = d as i32 * 3 / 120;
                if n == 0 { (d as i32).signum() } else { n }
            };
            if let Ok(e) = CGEvent::new_scroll_event(self.source.clone(), ScrollEventUnit::LINE, 2, lines(dy), lines(dx), 0) {
                self.post(e);
            }
        }

        pub fn key(&mut self, code: u16, down: bool) {
            if let Some(flag) = modifier_flag(code) {
                if down {
                    self.flags.insert(flag);
                } else {
                    self.flags.remove(flag);
                }
            }
            let Some(mac) = mac_key(code) else { return };
            if let Ok(e) = CGEvent::new_keyboard_event(self.source.clone(), mac as CGKeyCode, down) {
                if modifier_flag(code).is_some() {
                    e.set_type(CGEventType::FlagsChanged);
                }
                self.post(e);
            }
            self.keys.retain(|&k| k != code);
            if down {
                self.keys.push(code);
            }
        }

        pub fn release_all(&mut self) {
            for code in std::mem::take(&mut self.keys) {
                self.key(code, false);
            }
            self.keys.clear();
            self.flags = CGEventFlags::CGEventFlagNull;
            let held = self.held;
            for (b, down) in held.iter().enumerate() {
                if *down {
                    self.button(b as u8, false, self.last_click.2, self.last_click.3);
                }
            }
        }
    }

    /// PC shortcuts keep working on the Mac: Ctrl acts as Command, the Windows key as Control.
    fn modifier_flag(code: u16) -> Option<CGEventFlags> {
        Some(match code {
            29 | 97 => CGEventFlags::CGEventFlagCommand,
            125 | 126 => CGEventFlags::CGEventFlagControl,
            42 | 54 => CGEventFlags::CGEventFlagShift,
            56 | 100 => CGEventFlags::CGEventFlagAlternate,
            _ => return None,
        })
    }

    /// Linux evdev key code -> macOS virtual key code (Carbon's kVK_ values).
    fn mac_key(code: u16) -> Option<u16> {
        Some(match code {
            1 => 0x35,                                                            // Esc
            2 => 0x12, 3 => 0x13, 4 => 0x14, 5 => 0x15, 6 => 0x17,                // 1-5
            7 => 0x16, 8 => 0x1A, 9 => 0x1C, 10 => 0x19, 11 => 0x1D,              // 6-0
            12 => 0x1B, 13 => 0x18, 14 => 0x33, 15 => 0x30,                       // - = Backspace Tab
            16 => 0x0C, 17 => 0x0D, 18 => 0x0E, 19 => 0x0F, 20 => 0x11,           // q w e r t
            21 => 0x10, 22 => 0x20, 23 => 0x22, 24 => 0x1F, 25 => 0x23,           // y u i o p
            26 => 0x21, 27 => 0x1E, 28 => 0x24,                                   // [ ] Enter
            29 => 0x37,                                                           // left Ctrl -> Command
            30 => 0x00, 31 => 0x01, 32 => 0x02, 33 => 0x03, 34 => 0x05,           // a s d f g
            35 => 0x04, 36 => 0x26, 37 => 0x28, 38 => 0x25,                       // h j k l
            39 => 0x29, 40 => 0x27, 41 => 0x32,                                   // ; ' `
            42 => 0x38, 43 => 0x2A,                                               // left Shift, backslash
            44 => 0x06, 45 => 0x07, 46 => 0x08, 47 => 0x09, 48 => 0x0B,           // z x c v b
            49 => 0x2D, 50 => 0x2E, 51 => 0x2B, 52 => 0x2F, 53 => 0x2C,           // n m , . /
            54 => 0x3C, 55 => 0x43, 56 => 0x3A, 57 => 0x31, 58 => 0x39,           // right Shift, keypad *, left Alt -> Option, Space, Caps Lock
            59 => 0x7A, 60 => 0x78, 61 => 0x63, 62 => 0x76, 63 => 0x60,           // F1-F5
            64 => 0x61, 65 => 0x62, 66 => 0x64, 67 => 0x65, 68 => 0x6D,           // F6-F10
            69 => 0x47,                                                           // Num Lock -> keypad Clear
            71 => 0x59, 72 => 0x5B, 73 => 0x5C, 74 => 0x4E,                       // keypad 7 8 9 -
            75 => 0x56, 76 => 0x57, 77 => 0x58, 78 => 0x45,                       // keypad 4 5 6 +
            79 => 0x53, 80 => 0x54, 81 => 0x55, 82 => 0x52, 83 => 0x41,           // keypad 1 2 3 0 .
            86 => 0x0A,                                                           // the extra key next to left Shift
            87 => 0x67, 88 => 0x6F,                                               // F11 F12
            96 => 0x4C, 97 => 0x36, 98 => 0x4B,                                   // keypad Enter, right Ctrl -> right Command, keypad /
            100 => 0x3D,                                                          // right Alt -> right Option
            102 => 0x73, 103 => 0x7E, 104 => 0x74, 105 => 0x7B, 106 => 0x7C,      // Home Up PageUp Left Right
            107 => 0x77, 108 => 0x7D, 109 => 0x79, 110 => 0x72, 111 => 0x75,      // End Down PageDown Insert(Help) Delete
            113 => 0x4A, 114 => 0x49, 115 => 0x48,                                // mute, volume down, volume up
            125 => 0x3B, 126 => 0x3E,                                             // Windows keys -> Control
            183 => 0x69, 184 => 0x6B, 185 => 0x71, 186 => 0x6A,                   // F13-F16
            _ => return None,
        })
    }

    pub fn clipboard_get() -> Option<String> {
        let out = std::process::Command::new("pbpaste").output().ok()?;
        String::from_utf8(out.stdout).ok().filter(|s| !s.is_empty())
    }

    pub fn clipboard_set(text: &str) {
        use std::io::Write;
        if let Ok(mut child) = std::process::Command::new("pbcopy").stdin(std::process::Stdio::piped()).spawn() {
            if let Some(mut stdin) = child.stdin.take() {
                let _ = stdin.write_all(text.as_bytes());
            }
            let _ = child.wait();
        }
    }
}
