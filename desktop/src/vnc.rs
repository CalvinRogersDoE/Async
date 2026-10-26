//! A VNC (RFB 3.3/3.8) session into a paired computer whose screen sharing speaks VNC: macOS
//! Screen Sharing, or a VNC server such as x11vnc on Linux. It is used like the Remote Desktop
//! session in rdp.rs and reports the same `rdp::Out` messages.
//!
//! A port of the Windows app's viewer (app/src/VncView.cs). Sign-in is Apple's own (security type
//! 30, the Mac's user name and password), the classic VNC password (type 2) or none (type 1). The
//! screen comes as 32-bit BGRX pixels in the ZRLE, CopyRect or Raw encodings, with DesktopSize for
//! resolution changes, Cursor shapes (read and dropped: the portal draws no remote cursor), Fence
//! and ContinuousUpdates.
//!
//! Three threads: one reads and decodes the server's messages into a framebuffer, one writes to the
//! server (holding mouse moves back to one per 17 ms), and one hands whole-screen copies to the app
//! at most about 60 times a second.

use std::collections::HashMap;
use std::io::{self, BufReader, Read, Write};
use std::net::{Shutdown, TcpStream, ToSocketAddrs};
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use aes::Aes128;
use aes::cipher::{BlockEncrypt, KeyInit};
use anyhow::Result;
use des::Des;
use flate2::{Decompress, FlushDecompress};
use md5::{Digest, Md5};
use num_bigint::BigUint;
use winit::event::{ElementState, KeyEvent, MouseScrollDelta, WindowEvent};
use winit::keyboard::{Key, KeyLocation, NamedKey, PhysicalKey};
use winit::platform::modifier_supplement::KeyEventExtModifierSupplement;

use crate::rdp::Out;

const RAW: i32 = 0;
const COPY_RECT: i32 = 1;
const ZRLE: i32 = 16;
const DESKTOP_SIZE: i32 = -223;
// Pseudo-encodings: the cursor's shape sent apart from the screen, and the server sending changes
// without being asked for each one.
const CURSOR_SHAPE: i32 = -239;
const FENCE: i32 = -312;
const CONTINUOUS_UPDATES: i32 = -313;
/// Security type 30, Apple's Diffie-Hellman sign-in with the Mac's user name and password.
const APPLE_AUTH: u8 = 30;

// Mouse moves go out at most every 17 ms, latest position only, as noVNC and TigerVNC do. A fast
// mouse would otherwise flood the server (a Mac handles each one before the next screen update),
// which makes everything on the remote screen move in jumps.
const MOVE_INTERVAL: Duration = Duration::from_millis(17);
/// The shortest time between two frames handed to the app.
const FRAME_INTERVAL: Duration = Duration::from_millis(16);
/// Trackpad scrolling in pixels that makes one wheel click.
const PIXELS_PER_NOTCH: f64 = 40.0;

pub struct Session {
    tx: Sender<Msg>,
    shared: Arc<Shared>,
    buttons: u8,
    pos: (u16, u16),
    /// Keys held down, with the keysym each went down as (so it comes up as the same one).
    keys: HashMap<PhysicalKey, u32>,
    wheel: (f64, f64),
}

/// What the app's side and the session's threads share.
struct Shared {
    /// Closed on purpose: end quietly.
    stop: AtomicBool,
    connected: AtomicBool,
    /// The connection is over, however it ended.
    ended: AtomicBool,
    /// The screen's size, width << 16 | height.
    size: AtomicU32,
    socket: Mutex<Option<TcpStream>>,
    fb: Mutex<Framebuffer>,
    changed: Condvar,
}

struct Framebuffer {
    pixels: Vec<u32>,
    w: u16,
    h: u16,
    dirty: bool,
}

enum Msg {
    /// A mouse move: may wait for the move interval, and is replaced by a newer one meanwhile.
    Move([u8; 6]),
    Send(Vec<u8>),
    Quit,
}

/// A failure, worded to show a person.
struct Failure(String);

impl From<io::Error> for Failure {
    fn from(e: io::Error) -> Self {
        if matches!(e.kind(), io::ErrorKind::UnexpectedEof | io::ErrorKind::ConnectionReset | io::ErrorKind::ConnectionAborted) {
            Failure("The screen sharing connection closed.".into())
        } else {
            Failure(format!("Screen sharing connection lost: {e}"))
        }
    }
}

fn fail<T>(why: impl Into<String>) -> Result<T, Failure> {
    Err(Failure(why.into()))
}

impl Session {
    /// Connects and signs in, on threads of its own: with the computer's own user name and password
    /// where it offers that (a Mac's Screen Sharing), otherwise with a VNC password, or with none
    /// if it asks for none. `rt` isn't needed (the session uses plain threads); it is taken so this
    /// starts like the Remote Desktop session.
    pub fn start(_rt: &tokio::runtime::Handle, host: &str, port: u16, user: &str, password: &str, report: impl Fn(Out) + Send + 'static) -> Result<Self> {
        let shared = Arc::new(Shared {
            stop: AtomicBool::new(false),
            connected: AtomicBool::new(false),
            ended: AtomicBool::new(false),
            size: AtomicU32::new(0),
            socket: Mutex::new(None),
            fb: Mutex::new(Framebuffer { pixels: Vec::new(), w: 0, h: 0, dirty: false }),
            changed: Condvar::new(),
        });
        let (tx, rx) = mpsc::channel();
        let report = Arc::new(Mutex::new(report));
        let (host, user, password) = (host.to_string(), user.to_string(), password.to_string());
        let (s, t) = (shared.clone(), tx.clone());
        std::thread::Builder::new().name("vnc-session".into()).spawn(move || {
            let send = |out: Out| (report.lock().unwrap())(out);
            let pump = {
                let (s, report) = (s.clone(), report.clone());
                std::thread::Builder::new().name("vnc-frames".into()).spawn(move || pump_frames(&s, |out| (report.lock().unwrap())(out)))
            };
            let result = run(&s, &host, port, &user, &password, rx, t, &send);
            s.stop_threads();
            if let Ok(pump) = pump {
                let _ = pump.join();
            }
            if !s.stop.load(Ordering::SeqCst) {
                let why = match result {
                    Ok(()) => "The connection closed.".to_string(),
                    Err(Failure(why)) => why,
                };
                send(Out::Ended(why));
            }
        })?;
        Ok(Self { tx, shared, buttons: 0, pos: (0, 0), keys: HashMap::new(), wheel: (0.0, 0.0) })
    }

    /// Ends the session (after letting go of anything held down); it reports nothing more.
    pub fn close(&mut self) {
        if self.shared.stop.load(Ordering::SeqCst) {
            return;
        }
        self.release_all();
        self.shared.stop.store(true, Ordering::SeqCst);
        // The writer sends what's queued, then shuts the connection, which ends the reader.
        let _ = self.tx.send(Msg::Quit);
        if !self.shared.connected.load(Ordering::SeqCst) {
            self.shared.shutdown();
        }
    }

    /// Lets go of every key and mouse button held down on the other computer.
    pub fn release_all(&mut self) {
        for (_, sym) in std::mem::take(&mut self.keys) {
            self.send(key_event(sym, false));
        }
        if self.buttons != 0 {
            self.buttons = 0;
            self.send(pointer_event(0, self.pos).to_vec());
        }
    }

    pub fn mouse_to(&mut self, x: f64, y: f64) {
        let size = self.shared.size.load(Ordering::Relaxed);
        let (w, h) = ((size >> 16) as f64, (size & 0xffff) as f64);
        self.pos = (x.clamp(0.0, (w - 1.0).max(0.0)) as u16, y.clamp(0.0, (h - 1.0).max(0.0)) as u16);
        if self.shared.connected.load(Ordering::Relaxed) {
            let _ = self.tx.send(Msg::Move(pointer_event(self.buttons, self.pos)));
        }
    }

    /// Forwards keyboard, mouse and wheel input from the portal window; `to_desktop` maps a window
    /// position to the remote screen, or None outside the picture.
    pub fn window_input(&mut self, event: &WindowEvent, to_desktop: impl Fn(f64, f64) -> Option<(f64, f64)>) {
        match event {
            WindowEvent::KeyboardInput { event, .. } => self.key(event),
            WindowEvent::CursorMoved { position, .. } => {
                if let Some((x, y)) = to_desktop(position.x, position.y) {
                    self.mouse_to(x, y);
                }
            }
            WindowEvent::MouseInput { state, button, .. } => {
                let bit = match button {
                    winit::event::MouseButton::Left => 1,
                    winit::event::MouseButton::Middle => 2,
                    winit::event::MouseButton::Right => 4,
                    _ => return,
                };
                match state {
                    ElementState::Pressed => self.buttons |= bit,
                    ElementState::Released => self.buttons &= !bit,
                }
                self.send(pointer_event(self.buttons, self.pos).to_vec());
            }
            WindowEvent::MouseWheel { delta, .. } => {
                let (x, y) = match *delta {
                    MouseScrollDelta::LineDelta(x, y) => (x as f64, y as f64),
                    MouseScrollDelta::PixelDelta(p) => (p.x / PIXELS_PER_NOTCH, p.y / PIXELS_PER_NOTCH),
                };
                self.wheel.0 += x;
                self.wheel.1 += y;
                // Wheel clicks are buttons 4 and 5 (up, down), 6 and 7 (left, right): pressed and let go.
                let (mut wx, mut wy) = self.wheel;
                while wy.abs() >= 1.0 {
                    self.click(if wy > 0.0 { 8 } else { 16 });
                    wy -= wy.signum();
                }
                while wx.abs() >= 1.0 {
                    self.click(if wx > 0.0 { 32 } else { 64 });
                    wx -= wx.signum();
                }
                self.wheel = (wx, wy);
            }
            WindowEvent::Focused(false) => self.release_all(),
            _ => {}
        }
    }

    fn click(&mut self, bit: u8) {
        self.send(pointer_event(self.buttons | bit, self.pos).to_vec());
        self.send(pointer_event(self.buttons, self.pos).to_vec());
    }

    fn key(&mut self, event: &KeyEvent) {
        match event.state {
            ElementState::Pressed => {
                let sym = self.keys.get(&event.physical_key).copied().or_else(|| keysym(event));
                if let Some(sym) = sym {
                    self.keys.insert(event.physical_key, sym);
                    self.send(key_event(sym, true));
                }
            }
            ElementState::Released => {
                if let Some(sym) = self.keys.remove(&event.physical_key).or_else(|| keysym(event)) {
                    self.send(key_event(sym, false));
                }
            }
        }
    }

    fn send(&mut self, msg: Vec<u8>) {
        if self.shared.connected.load(Ordering::Relaxed) {
            let _ = self.tx.send(Msg::Send(msg));
        }
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        self.close();
    }
}

impl Shared {
    fn shutdown(&self) {
        if let Some(s) = self.socket.lock().unwrap().as_ref() {
            let _ = s.shutdown(Shutdown::Both);
        }
    }

    /// Wakes the frame thread so it notices the session is over.
    fn stop_threads(&self) {
        self.connected.store(false, Ordering::SeqCst);
        let _fb = self.fb.lock().unwrap();
        self.ended.store(true, Ordering::SeqCst);
        self.changed.notify_all();
    }

    fn over(&self) -> bool {
        self.stop.load(Ordering::SeqCst) || self.ended.load(Ordering::SeqCst)
    }
}

/// Hands the app a copy of the screen whenever it changed, at most once per frame interval.
fn pump_frames(s: &Shared, report: impl Fn(Out)) {
    loop {
        let mut fb = s.fb.lock().unwrap();
        while !fb.dirty {
            if s.over() {
                return;
            }
            fb = s.changed.wait_timeout(fb, Duration::from_millis(250)).unwrap().0;
        }
        fb.dirty = false;
        let (pixels, width, height) = (fb.pixels.clone(), fb.w, fb.h);
        drop(fb);
        if s.over() {
            return;
        }
        let sent = Instant::now();
        report(Out::Frame { pixels, width, height });
        std::thread::sleep(FRAME_INTERVAL.saturating_sub(sent.elapsed()));
    }
}

fn run(s: &Arc<Shared>, host: &str, port: u16, user: &str, password: &str, rx: Receiver<Msg>, tx: Sender<Msg>, report: &impl Fn(Out)) -> Result<(), Failure> {
    let stream = connect(host, port)?;
    let _ = stream.set_nodelay(true);
    *s.socket.lock().unwrap() = Some(stream.try_clone()?);
    if s.stop.load(Ordering::SeqCst) {
        return Ok(());
    }
    // A server that stops answering mid-sign-in shouldn't leave the portal waiting for ever.
    stream.set_read_timeout(Some(Duration::from_secs(20)))?;
    let mut c = Conn { r: BufReader::with_capacity(1 << 16, stream.try_clone()?), tx, w: 0, h: 0, continuous: false, z: Zlib::new(), stats: Stats::new() };
    let mut out = stream.try_clone()?;
    c.handshake(&mut out, s, user, password)?;
    stream.set_read_timeout(None)?;
    if s.stop.load(Ordering::SeqCst) {
        return Ok(());
    }

    let writer = std::thread::Builder::new().name("vnc-writer".into()).spawn(move || write_loop(out, rx))?;
    s.connected.store(true, Ordering::SeqCst);
    report(Out::LoggedIn);
    c.request_update(false);
    let result = loop {
        if let Err(e) = c.read_server_message(s) {
            break Err(e);
        }
    };
    s.connected.store(false, Ordering::SeqCst);
    let _ = c.tx.send(Msg::Quit);
    let _ = writer.join();
    result
}

fn connect(host: &str, port: u16) -> Result<TcpStream, Failure> {
    let addrs = (host, port).to_socket_addrs().map_err(|e| Failure(format!("Couldn't find {host}: {e}")))?;
    let mut last = None;
    for addr in addrs {
        match TcpStream::connect_timeout(&addr, Duration::from_secs(10)) {
            Ok(s) => return Ok(s),
            Err(e) => last = Some(e),
        }
    }
    fail(match last {
        Some(e) => format!("Couldn't reach the screen sharing server: {e}"),
        None => format!("Couldn't find {host}."),
    })
}

/// Sends what the session queues, holding mouse moves back to one per move interval (the latest
/// one) and sending a held-back move before anything else (so a click lands where the pointer is).
fn write_loop(mut out: TcpStream, rx: Receiver<Msg>) {
    let mut pending: Option<[u8; 6]> = None;
    let mut last_move = Instant::now() - MOVE_INTERVAL;
    fn flush(out: &mut TcpStream, pending: &mut Option<[u8; 6]>, last_move: &mut Instant) -> io::Result<()> {
        if let Some(m) = pending.take() {
            out.write_all(&m)?;
            *last_move = Instant::now();
        }
        Ok(())
    }
    loop {
        let msg = if pending.is_some() {
            match rx.recv_timeout((last_move + MOVE_INTERVAL).saturating_duration_since(Instant::now())) {
                Ok(m) => Some(m),
                Err(RecvTimeoutError::Timeout) => None,
                Err(RecvTimeoutError::Disconnected) => break,
            }
        } else {
            match rx.recv() {
                Ok(m) => Some(m),
                Err(_) => break,
            }
        };
        let ok = match msg {
            None => flush(&mut out, &mut pending, &mut last_move),
            Some(Msg::Move(m)) => {
                pending = Some(m);
                if last_move.elapsed() >= MOVE_INTERVAL { flush(&mut out, &mut pending, &mut last_move) } else { Ok(()) }
            }
            Some(Msg::Send(bytes)) => flush(&mut out, &mut pending, &mut last_move).and_then(|_| out.write_all(&bytes)),
            Some(Msg::Quit) => {
                let _ = flush(&mut out, &mut pending, &mut last_move);
                break;
            }
        };
        if ok.is_err() {
            break;
        }
    }
    let _ = out.shutdown(Shutdown::Both);
}

/// The reading side of a connection.
struct Conn {
    r: BufReader<TcpStream>,
    tx: Sender<Msg>,
    w: u16,
    h: u16,
    /// The server keeps sending changes without being asked each time (the ContinuousUpdates extension).
    continuous: bool,
    z: Zlib,
    stats: Stats,
}

impl Conn {
    fn handshake(&mut self, out: &mut TcpStream, s: &Shared, user: &str, password: &str) -> Result<(), Failure> {
        let server = self.bytes(12)?;
        if !server.starts_with(b"RFB ") {
            return fail("That isn't a VNC screen sharing server.");
        }
        let v38 = server.as_slice() >= b"RFB 003.007".as_slice();
        out.write_all(if v38 { b"RFB 003.008\n" } else { b"RFB 003.003\n" })?;

        let security = if v38 {
            let count = self.u8()?;
            if count == 0 {
                return fail(self.reason()?);
            }
            let offered = self.bytes(count as usize)?;
            let (has_user, has_password) = (!user.is_empty(), !password.is_empty());
            // Apple's own sign-in (a Mac user name and password) first: newer macOS often turns the
            // old VNC password away even when it's switched on.
            let security = if offered.contains(&APPLE_AUTH) && has_user && has_password {
                APPLE_AUTH as u32
            } else if offered.contains(&2) && has_password {
                2
            } else if offered.contains(&1) {
                1
            } else if offered.contains(&APPLE_AUTH) {
                return fail("This Mac needs its user name and password. In Async's settings, enter the name and password you log into the Mac with.");
            } else if offered.contains(&2) {
                return fail("This computer's screen sharing needs a password. Enter it in Async's settings.");
            } else {
                return fail("This computer's screen sharing uses a sign-in Async doesn't know.");
            };
            out.write_all(&[security as u8])?;
            security
        } else {
            self.u32()?
        };

        if security == 0 {
            return fail(self.reason()?);
        }
        if security == 2 {
            let challenge = self.bytes(16)?;
            out.write_all(&vnc_auth(password, &challenge))?;
        } else if security == APPLE_AUTH as u32 {
            let reply = self.apple_sign_in(user, password)?;
            out.write_all(&reply)?;
        }
        if (security == 2 || security == APPLE_AUTH as u32 || v38) && self.u32()? != 0 {
            let why = if v38 { self.reason()? } else { String::new() };
            let why = if why.is_empty() { why } else { format!(" ({why})") };
            return fail(if security == APPLE_AUTH as u32 {
                format!("The Mac turned down that user name and password{why}. Use the name and password you log into the Mac with, in Async's settings.")
            } else {
                format!("Wrong screen sharing password{why}. Enter it again in Async's settings.")
            });
        }

        out.write_all(&[1])?; // ClientInit: share the screen with anyone else watching
        let (w, h) = (self.u16()?, self.u16()?);
        self.bytes(16)?; // the server's pixel format; ours replaces it below
        let name_len = self.u32()? as usize;
        let name = self.bytes(name_len)?;
        eprintln!("VNC: connected to \"{}\", {w}x{h}", String::from_utf8_lossy(&name));
        self.resize(s, w, h);

        // 32-bit little-endian BGRX: each pixel read as a little-endian u32 is 0x00RRGGBB.
        let mut pf = [0u8; 20];
        pf[0] = 0;
        pf[4] = 32;
        pf[5] = 24;
        pf[6] = 0;
        pf[7] = 1;
        pf[8..10].copy_from_slice(&255u16.to_be_bytes());
        pf[10..12].copy_from_slice(&255u16.to_be_bytes());
        pf[12..14].copy_from_slice(&255u16.to_be_bytes());
        pf[14] = 16;
        pf[15] = 8;
        pf[16] = 0;
        out.write_all(&pf)?;

        let encodings = [ZRLE, COPY_RECT, RAW, DESKTOP_SIZE, CURSOR_SHAPE, FENCE, CONTINUOUS_UPDATES];
        let mut enc = vec![2, 0];
        enc.extend_from_slice(&(encodings.len() as u16).to_be_bytes());
        for e in encodings {
            enc.extend_from_slice(&e.to_be_bytes());
        }
        out.write_all(&enc)?;
        Ok(())
    }

    /// Apple's sign-in: agree on a key with Diffie-Hellman (the Mac sends the generator, prime and
    /// its public key), then send the user name and password (64 bytes each, NUL-terminated, padded
    /// with random bytes) encrypted with AES-128-ECB under MD5(shared secret), then our public key.
    fn apple_sign_in(&mut self, user: &str, password: &str) -> Result<Vec<u8>, Failure> {
        let generator = self.bytes(2)?;
        let key_length = self.u16()? as usize;
        let prime = self.bytes(key_length)?;
        let their_public = self.bytes(key_length)?;

        let p = BigUint::from_bytes_be(&prime);
        if p.bits() < 2 {
            return fail("The Mac sent a sign-in Async can't use.");
        }
        let g = BigUint::from_bytes_be(&generator);
        let secret = BigUint::from_bytes_be(&random(key_length)?) % &p;
        let our_public = g.modpow(&secret, &p);
        let shared = BigUint::from_bytes_be(&their_public).modpow(&secret, &p);

        let fixed = |v: &BigUint| {
            let b = v.to_bytes_be();
            if b.len() >= key_length {
                return b;
            }
            let mut padded = vec![0u8; key_length - b.len()];
            padded.extend_from_slice(&b);
            padded
        };

        let mut credentials = random(128)?;
        let mut put = |text: &str, at: usize| {
            let bytes = text.as_bytes();
            let n = bytes.len().min(63);
            credentials[at..at + n].copy_from_slice(&bytes[..n]);
            credentials[at + n] = 0;
        };
        put(user, 0);
        put(password, 64);

        let key = Md5::digest(fixed(&shared));
        let aes = Aes128::new(&key);
        for block in credentials.chunks_exact_mut(16) {
            aes.encrypt_block(block.into());
        }
        credentials.extend_from_slice(&fixed(&our_public));
        Ok(credentials)
    }

    fn read_server_message(&mut self, s: &Shared) -> Result<(), Failure> {
        match self.u8()? {
            0 => self.read_update(s)?,
            1 => {
                // Colour map: not used in true colour.
                self.bytes(3)?;
                let n = self.u16()? as usize;
                self.bytes(6 * n)?;
            }
            2 => {} // bell
            3 => {
                // The remote clipboard changed; the portal doesn't take it over.
                self.bytes(3)?;
                let n = self.u32()? as usize;
                self.bytes(n)?;
            }
            150 => {
                // EndOfContinuousUpdates: the server can stream; turn it on (as noVNC does).
                if !self.continuous {
                    self.continuous = true;
                    self.enable_continuous();
                }
            }
            248 => {
                // A fence: answer it with the same payload (messages are handled strictly in order).
                self.bytes(3)?;
                let flags = self.u32()?;
                let n = self.u8()? as usize;
                let payload = self.bytes(n)?;
                if flags & 0x8000_0000 != 0 {
                    let mut reply = vec![248, 0, 0, 0];
                    reply.extend_from_slice(&(flags & 7).to_be_bytes());
                    reply.push(payload.len() as u8);
                    reply.extend_from_slice(&payload);
                    self.send(reply);
                }
            }
            _ => return fail("The screen sharing server sent something Async doesn't understand."),
        }
        Ok(())
    }

    fn read_update(&mut self, s: &Shared) -> Result<(), Failure> {
        self.bytes(1)?;
        let rects = self.u16()?;
        // Ask for the next one straight away, so the server gathers changes while this one decodes.
        if !self.continuous {
            self.request_update(true);
        }
        let mut changed = false;
        for _ in 0..rects {
            let (x, y, w, h) = (self.u16()?, self.u16()?, self.u16()?, self.u16()?);
            let encoding = self.u32()? as i32;
            self.stats.encodings.insert(encoding);
            match encoding {
                RAW => self.decode_raw(s, x, y, w, h)?,
                COPY_RECT => self.decode_copy_rect(s, x, y, w, h)?,
                ZRLE => self.decode_zrle(s, x, y, w, h)?,
                DESKTOP_SIZE => {
                    self.resize(s, w, h);
                    if self.continuous {
                        self.enable_continuous();
                    }
                }
                CURSOR_SHAPE => {
                    // The cursor's picture, then its 1-bit mask; the hotspot is in x and y.
                    let (w, h) = (w as usize, h as usize);
                    self.bytes(w * h * 4)?;
                    self.bytes(w.div_ceil(8) * h)?;
                    continue;
                }
                _ => return fail(format!("Unsupported VNC encoding {encoding}.")),
            }
            changed = true;
        }
        if changed {
            s.fb.lock().unwrap().dirty = true;
            s.changed.notify_all();
        }
        self.stats.update(self.w, self.h, self.continuous);
        Ok(())
    }

    fn resize(&mut self, s: &Shared, w: u16, h: u16) {
        let (w, h) = (w.max(1), h.max(1));
        (self.w, self.h) = (w, h);
        s.size.store((w as u32) << 16 | h as u32, Ordering::SeqCst);
        let mut fb = s.fb.lock().unwrap();
        // Not shown until the first update fills it in.
        *fb = Framebuffer { pixels: vec![0; w as usize * h as usize], w, h, dirty: false };
    }

    fn decode_raw(&mut self, s: &Shared, x: u16, y: u16, w: u16, h: u16) -> Result<(), Failure> {
        let mut row = vec![0u8; w as usize * 4];
        let mut px = vec![0u32; w as usize];
        for j in 0..h as usize {
            self.r.read_exact(&mut row)?;
            self.stats.bytes += row.len() as u64;
            for (p, b) in px.iter_mut().zip(row.chunks_exact(4)) {
                *p = u32::from_le_bytes([b[0], b[1], b[2], 0]);
            }
            write_row(&mut s.fb.lock().unwrap(), x as usize, y as usize + j, &px);
        }
        Ok(())
    }

    fn decode_copy_rect(&mut self, s: &Shared, x: u16, y: u16, w: u16, h: u16) -> Result<(), Failure> {
        let (sx, sy) = (self.u16()? as usize, self.u16()? as usize);
        let (x, y) = (x as usize, y as usize);
        let mut fb = s.fb.lock().unwrap();
        let (fw, fh) = (fb.w as usize, fb.h as usize);
        let w = (w as usize).min(fw.saturating_sub(sx)).min(fw.saturating_sub(x));
        let h = (h as usize).min(fh.saturating_sub(sy)).min(fh.saturating_sub(y));
        // Rows in the order that never overwrites one still to be copied (copy_within handles
        // overlap within a row).
        let down = y > sy;
        for k in 0..h {
            let j = if down { h - 1 - k } else { k };
            let from = (sy + j) * fw + sx;
            fb.pixels.copy_within(from..from + w, (y + j) * fw + x);
        }
        Ok(())
    }

    /// ZRLE: one zlib stream for the whole connection, 64×64 tiles, 3-byte pixels.
    fn decode_zrle(&mut self, s: &Shared, x: u16, y: u16, w: u16, h: u16) -> Result<(), Failure> {
        let length = self.u32()? as usize;
        let data = self.bytes(length)?;
        self.z.append(&data);
        let mut fb = s.fb.lock().unwrap();
        self.z.decode_tiles(&mut fb, x as usize, y as usize, w as usize, h as usize)
    }

    fn enable_continuous(&mut self) {
        let mut msg = vec![150, 1, 0, 0, 0, 0];
        msg.extend_from_slice(&self.w.to_be_bytes());
        msg.extend_from_slice(&self.h.to_be_bytes());
        self.send(msg);
    }

    fn request_update(&mut self, incremental: bool) {
        let mut msg = vec![3, incremental as u8, 0, 0, 0, 0];
        msg.extend_from_slice(&self.w.to_be_bytes());
        msg.extend_from_slice(&self.h.to_be_bytes());
        self.send(msg);
    }

    fn send(&self, msg: Vec<u8>) {
        let _ = self.tx.send(Msg::Send(msg));
    }

    fn bytes(&mut self, n: usize) -> Result<Vec<u8>, Failure> {
        let mut b = vec![0u8; n];
        self.r.read_exact(&mut b)?;
        self.stats.bytes += n as u64;
        Ok(b)
    }

    fn u8(&mut self) -> Result<u8, Failure> {
        Ok(self.bytes(1)?[0])
    }

    fn u16(&mut self) -> Result<u16, Failure> {
        let mut b = [0u8; 2];
        self.r.read_exact(&mut b)?;
        Ok(u16::from_be_bytes(b))
    }

    fn u32(&mut self) -> Result<u32, Failure> {
        let mut b = [0u8; 4];
        self.r.read_exact(&mut b)?;
        Ok(u32::from_be_bytes(b))
    }

    fn reason(&mut self) -> Result<String, Failure> {
        let n = self.u32()? as usize;
        Ok(String::from_utf8_lossy(&self.bytes(n)?).into_owned())
    }
}

/// Copies one row of pixels into the screen, clipped to it.
fn write_row(fb: &mut Framebuffer, x: usize, y: usize, src: &[u32]) {
    let (fw, fh) = (fb.w as usize, fb.h as usize);
    if y >= fh || x >= fw {
        return;
    }
    let n = src.len().min(fw - x);
    fb.pixels[y * fw + x..y * fw + x + n].copy_from_slice(&src[..n]);
}

/// ZRLE's zlib stream, which lasts the whole connection. Each rectangle's compressed bytes are
/// appended; decoding reads only what has arrived, and leftovers carry over to the next rectangle.
struct Zlib {
    d: Decompress,
    input: Vec<u8>,
    in_pos: usize,
    out: Vec<u8>,
    pos: usize,
    len: usize,
}

impl Zlib {
    fn new() -> Self {
        Self { d: Decompress::new(true), input: Vec::new(), in_pos: 0, out: vec![0; 1 << 16], pos: 0, len: 0 }
    }

    fn append(&mut self, data: &[u8]) {
        self.input.drain(..self.in_pos);
        self.in_pos = 0;
        self.input.extend_from_slice(data);
    }

    fn fill(&mut self) -> Result<(), Failure> {
        let (before_in, before_out) = (self.d.total_in(), self.d.total_out());
        let status = self.d.decompress(&self.input[self.in_pos..], &mut self.out, FlushDecompress::None);
        self.in_pos += (self.d.total_in() - before_in) as usize;
        self.pos = 0;
        self.len = (self.d.total_out() - before_out) as usize;
        if status.is_err() {
            return fail("The screen data was damaged.");
        }
        if self.len == 0 {
            return fail("Screen data ended early.");
        }
        Ok(())
    }

    fn byte(&mut self) -> Result<u8, Failure> {
        if self.pos == self.len {
            self.fill()?;
        }
        self.pos += 1;
        Ok(self.out[self.pos - 1])
    }

    fn pixel(&mut self) -> Result<u32, Failure> {
        if self.len - self.pos >= 3 {
            let o = &self.out[self.pos..self.pos + 3];
            self.pos += 3;
            return Ok(o[0] as u32 | (o[1] as u32) << 8 | (o[2] as u32) << 16);
        }
        Ok(self.byte()? as u32 | (self.byte()? as u32) << 8 | (self.byte()? as u32) << 16)
    }

    fn run(&mut self) -> Result<usize, Failure> {
        let mut run = 1;
        loop {
            let b = self.byte()?;
            run += b as usize;
            if b != 255 {
                return Ok(run);
            }
        }
    }

    fn decode_tiles(&mut self, fb: &mut Framebuffer, x: usize, y: usize, w: usize, h: usize) -> Result<(), Failure> {
        let mut tile = [0u32; 64 * 64];
        let mut palette = [0u32; 128];
        for ty in (y..y + h).step_by(64) {
            for tx in (x..x + w).step_by(64) {
                let (tw, th) = (64.min(x + w - tx), 64.min(y + h - ty));
                let n = tw * th;
                let sub = self.byte()? as usize;
                if sub == 0 {
                    for p in &mut tile[..n] {
                        *p = self.pixel()?;
                    }
                } else if sub == 1 {
                    let c = self.pixel()?;
                    tile[..n].fill(c);
                } else if sub <= 16 {
                    for p in &mut palette[..sub] {
                        *p = self.pixel()?;
                    }
                    let bits = if sub == 2 { 1 } else if sub <= 4 { 2 } else { 4 };
                    for j in 0..th {
                        let (mut byte, mut left) = (0u32, 0);
                        for i in 0..tw {
                            if left == 0 {
                                byte = self.byte()? as u32;
                                left = 8;
                            }
                            left -= bits;
                            tile[j * tw + i] = palette[((byte >> left) & ((1 << bits) - 1)) as usize];
                        }
                    }
                } else if sub == 128 {
                    let mut i = 0;
                    while i < n {
                        let c = self.pixel()?;
                        let run = self.run()?;
                        let end = (i + run).min(n);
                        tile[i..end].fill(c);
                        i = end;
                    }
                } else if sub >= 130 {
                    let size = sub - 128;
                    for p in &mut palette[..size] {
                        *p = self.pixel()?;
                    }
                    let mut i = 0;
                    while i < n {
                        let b = self.byte()? as usize;
                        let c = palette[b & 127];
                        let run = if b & 128 != 0 { self.run()? } else { 1 };
                        let end = (i + run).min(n);
                        tile[i..end].fill(c);
                        i = end;
                    }
                } else {
                    return fail(format!("Bad ZRLE tile type {sub}."));
                }
                for j in 0..th {
                    write_row(fb, tx, ty + j, &tile[j * tw..j * tw + tw]);
                }
            }
        }
        Ok(())
    }
}

/// VNC's DES challenge-response: the password (8 bytes, each bit-reversed) encrypts the challenge.
fn vnc_auth(password: &str, challenge: &[u8]) -> Vec<u8> {
    let mut key = [0u8; 8];
    // Latin-1, as VNC servers read it.
    for (k, c) in key.iter_mut().zip(password.chars()) {
        *k = (c as u32).min(255) as u8;
        *k = k.reverse_bits();
    }
    let des = Des::new(&key.into());
    let mut out = challenge.to_vec();
    for block in out.chunks_exact_mut(8) {
        des.encrypt_block(block.into());
    }
    out
}

fn random(n: usize) -> Result<Vec<u8>, Failure> {
    let mut b = vec![0u8; n];
    getrandom::fill(&mut b).map_err(|e| Failure(format!("Couldn't sign in: no random numbers ({e}).")))?;
    Ok(b)
}

fn pointer_event(buttons: u8, (x, y): (u16, u16)) -> [u8; 6] {
    let (x, y) = (x.to_be_bytes(), y.to_be_bytes());
    [5, buttons, x[0], x[1], y[0], y[1]]
}

fn key_event(sym: u32, down: bool) -> Vec<u8> {
    let mut msg = vec![4, down as u8, 0, 0];
    msg.extend_from_slice(&sym.to_be_bytes());
    msg
}

/// A key as an X11 keysym. Characters go unshifted; Shift and the other modifiers travel as keys
/// of their own.
fn keysym(event: &KeyEvent) -> Option<u32> {
    let right = event.location == KeyLocation::Right;
    let numpad = event.location == KeyLocation::Numpad;
    let key = event.key_without_modifiers();
    Some(match key {
        Key::Named(named) => match named {
            NamedKey::Backspace => 0xff08,
            NamedKey::Tab => 0xff09,
            NamedKey::Enter if numpad => 0xff8d,
            NamedKey::Enter => 0xff0d,
            NamedKey::Escape => 0xff1b,
            NamedKey::Delete => 0xffff,
            NamedKey::Insert => 0xff63,
            NamedKey::Home => 0xff50,
            NamedKey::End => 0xff57,
            NamedKey::PageUp => 0xff55,
            NamedKey::PageDown => 0xff56,
            NamedKey::ArrowLeft => 0xff51,
            NamedKey::ArrowUp => 0xff52,
            NamedKey::ArrowRight => 0xff53,
            NamedKey::ArrowDown => 0xff54,
            NamedKey::Shift => if right { 0xffe2 } else { 0xffe1 },
            NamedKey::Control => if right { 0xffe4 } else { 0xffe3 },
            NamedKey::Alt => if right { 0xffea } else { 0xffe9 },
            NamedKey::AltGraph => 0xfe03,
            NamedKey::Super | NamedKey::Meta => if right { 0xffec } else { 0xffeb },
            NamedKey::ContextMenu => 0xff67,
            NamedKey::CapsLock => 0xffe5,
            NamedKey::NumLock => 0xff7f,
            NamedKey::ScrollLock => 0xff14,
            NamedKey::PrintScreen => 0xff61,
            NamedKey::Pause => 0xff13,
            NamedKey::Space => 0x20,
            NamedKey::F1 => 0xffbe,
            NamedKey::F2 => 0xffbf,
            NamedKey::F3 => 0xffc0,
            NamedKey::F4 => 0xffc1,
            NamedKey::F5 => 0xffc2,
            NamedKey::F6 => 0xffc3,
            NamedKey::F7 => 0xffc4,
            NamedKey::F8 => 0xffc5,
            NamedKey::F9 => 0xffc6,
            NamedKey::F10 => 0xffc7,
            NamedKey::F11 => 0xffc8,
            NamedKey::F12 => 0xffc9,
            NamedKey::F13 => 0xffca,
            NamedKey::F14 => 0xffcb,
            NamedKey::F15 => 0xffcc,
            NamedKey::F16 => 0xffcd,
            NamedKey::F17 => 0xffce,
            NamedKey::F18 => 0xffcf,
            NamedKey::F19 => 0xffd0,
            NamedKey::F20 => 0xffd1,
            _ => return None,
        },
        Key::Character(text) => {
            let mut chars = text.chars();
            let c = chars.next()?;
            if chars.next().is_some() {
                return None;
            }
            if numpad {
                match c {
                    '0'..='9' => return Some(0xffb0 + (c as u32 - '0' as u32)),
                    '*' => return Some(0xffaa),
                    '+' => return Some(0xffab),
                    '-' => return Some(0xffad),
                    '.' | ',' => return Some(0xffae),
                    '/' => return Some(0xffaf),
                    _ => {}
                }
            }
            let c = c.to_lowercase().next().unwrap_or(c) as u32;
            match c {
                0x20..=0x7e | 0xa0..=0xff => c,
                0x100.. => 0x0100_0000 | c,
                _ => return None,
            }
        }
        _ => return None,
    })
}

/// What arrives, written to the log every few seconds while connected.
struct Stats {
    bytes: u64,
    updates: u32,
    encodings: std::collections::BTreeSet<i32>,
    since: Instant,
}

impl Stats {
    fn new() -> Self {
        Self { bytes: 0, updates: 0, encodings: Default::default(), since: Instant::now() }
    }

    fn update(&mut self, w: u16, h: u16, continuous: bool) {
        self.updates += 1;
        let seconds = self.since.elapsed().as_secs_f64();
        if seconds < 5.0 {
            return;
        }
        let encodings: Vec<String> = self.encodings.iter().map(|e| e.to_string()).collect();
        eprintln!(
            "VNC {w}x{h}: {:.1} updates/s, {:.2} MB/s, encodings {}{}",
            self.updates as f64 / seconds,
            self.bytes as f64 / seconds / 1e6,
            encodings.join(","),
            if continuous { ", continuous" } else { "" }
        );
        (self.updates, self.bytes, self.since) = (0, 0, Instant::now());
    }
}
