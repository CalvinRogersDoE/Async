//! Mouse mode, the sending side's grip on this computer's mouse and keyboard: while the pointer is
//! over on another computer, every mouse and key event here is taken (so nothing happens locally)
//! and handed on as what [`share`](crate::share) sends.
//!
//! Linux (X11): the pointer and keyboard are grabbed; the pointer waits, invisible, in the middle of
//! the screen it left from and is put back there after each move, so moves in every direction can
//! be measured. macOS: an event tap swallows the events; the cursor is hidden and detached from the
//! mouse, which keeps reporting how far it moved. Both need nothing beyond what Async already asks
//! for (on macOS, Accessibility).

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use anyhow::Result;

#[derive(Debug, Clone, Copy)]
pub enum Captured {
    /// Pixels (desktop units).
    Move(i32, i32),
    /// 1 left, 2 middle, 3 right, 4 back, 5 forward.
    Button(u8, bool),
    /// 1/120 notch; positive is away from you / to the right.
    Wheel(i16, i16),
    /// Linux evdev code.
    Key(u16, bool),
    /// Ctrl+Alt+Up: bring the pointer back.
    Recall,
}

/// Running capture; dropping it gives the mouse and keyboard back.
pub struct Capture {
    stop: Arc<AtomicBool>,
    #[cfg(target_os = "macos")]
    run_loop: Arc<std::sync::Mutex<Option<usize>>>,
    thread: Option<std::thread::JoinHandle<()>>,
}

impl Drop for Capture {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        #[cfg(target_os = "macos")]
        if let Some(rl) = *self.run_loop.lock().unwrap() {
            use core_foundation::base::TCFType;
            unsafe { core_foundation::runloop::CFRunLoop::wrap_under_get_rule(rl as _).stop() };
        }
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

/// Watches held keys for the recall shortcut.
#[derive(Default)]
struct Keys(Vec<u16>);

impl Keys {
    /// Records a key; true when it completes Ctrl+Alt+Up.
    fn press(&mut self, code: u16, down: bool) -> bool {
        self.0.retain(|&k| k != code);
        if down {
            self.0.push(code);
        }
        let held = |a: u16, b: u16| self.0.contains(&a) || self.0.contains(&b);
        down && code == 103 && held(29, 97) && held(56, 100)
    }
}

// ---------- Linux (X11) ----------

#[cfg(target_os = "linux")]
pub fn start(center: (i32, i32), on: impl Fn(Captured) + Send + 'static) -> Result<Capture> {
    use x11rb::connection::Connection;
    use x11rb::protocol::Event;
    use x11rb::protocol::xproto::{ConnectionExt as _, EventMask, GrabMode, GrabStatus};

    let (conn, screen) = x11rb::connect(None)?;
    let root = conn.setup().roots[screen].root;
    // An invisible cursor: a 1x1 pixmap with nothing set in its mask.
    let pixmap = conn.generate_id()?;
    conn.create_pixmap(1, pixmap, root, 1, 1)?;
    let cursor = conn.generate_id()?;
    conn.create_cursor(cursor, pixmap, pixmap, 0, 0, 0, 0, 0, 0, 0, 0)?;
    conn.warp_pointer(x11rb::NONE, root, 0, 0, 0, 0, center.0 as i16, center.1 as i16)?;

    // Another program (a drag that's just ending) may hold a grab for a moment: retry briefly.
    let mask = EventMask::POINTER_MOTION | EventMask::BUTTON_PRESS | EventMask::BUTTON_RELEASE;
    let mut grabbed = false;
    for _ in 0..40 {
        let p = conn.grab_pointer(false, root, mask, GrabMode::ASYNC, GrabMode::ASYNC, x11rb::NONE, cursor, x11rb::CURRENT_TIME)?.reply()?;
        if p.status == GrabStatus::SUCCESS {
            grabbed = true;
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(25));
    }
    if !grabbed {
        anyhow::bail!("another program is holding the mouse");
    }
    let k = conn.grab_keyboard(false, root, x11rb::CURRENT_TIME, GrabMode::ASYNC, GrabMode::ASYNC)?.reply()?;
    if k.status != GrabStatus::SUCCESS {
        let _ = conn.ungrab_pointer(x11rb::CURRENT_TIME);
        let _ = conn.flush();
        anyhow::bail!("another program is holding the keyboard");
    }
    conn.flush()?;

    let stop = Arc::new(AtomicBool::new(false));
    let stopping = stop.clone();
    let thread = std::thread::Builder::new().name("capture".into()).spawn(move || {
        let mut keys = Keys::default();
        while !stopping.load(Ordering::Relaxed) {
            let event = match conn.poll_for_event() {
                Ok(Some(e)) => e,
                Ok(None) => {
                    std::thread::sleep(std::time::Duration::from_millis(1));
                    continue;
                }
                Err(_) => break,
            };
            match event {
                Event::MotionNotify(m) => {
                    let (dx, dy) = (m.root_x as i32 - center.0, m.root_y as i32 - center.1);
                    if dx != 0 || dy != 0 {
                        on(Captured::Move(dx, dy));
                        let _ = conn.warp_pointer(x11rb::NONE, root, 0, 0, 0, 0, center.0 as i16, center.1 as i16);
                        let _ = conn.flush();
                    }
                }
                Event::ButtonPress(b) | Event::ButtonRelease(b) => {
                    let down = b.response_type & 0x7f == x11rb::protocol::xproto::BUTTON_PRESS_EVENT;
                    match b.detail {
                        1 => on(Captured::Button(1, down)),
                        2 => on(Captured::Button(2, down)),
                        3 => on(Captured::Button(3, down)),
                        8 => on(Captured::Button(4, down)),
                        9 => on(Captured::Button(5, down)),
                        4 if down => on(Captured::Wheel(120, 0)),
                        5 if down => on(Captured::Wheel(-120, 0)),
                        6 if down => on(Captured::Wheel(0, -120)),
                        7 if down => on(Captured::Wheel(0, 120)),
                        _ => {}
                    }
                }
                Event::KeyPress(k) | Event::KeyRelease(k) => {
                    let down = k.response_type & 0x7f == x11rb::protocol::xproto::KEY_PRESS_EVENT;
                    let code = (k.detail as u16).saturating_sub(8);
                    if keys.press(code, down) {
                        on(Captured::Recall);
                    } else {
                        on(Captured::Key(code, down));
                    }
                }
                _ => {}
            }
        }
        let _ = conn.ungrab_keyboard(x11rb::CURRENT_TIME);
        let _ = conn.ungrab_pointer(x11rb::CURRENT_TIME);
        let _ = conn.free_cursor(cursor);
        let _ = conn.free_pixmap(pixmap);
        let _ = conn.flush();
    })?;
    Ok(Capture { stop, thread: Some(thread) })
}

/// Puts this computer's pointer at a desktop point (where it comes back).
#[cfg(target_os = "linux")]
pub fn warp(x: i32, y: i32) {
    use x11rb::connection::Connection;
    use x11rb::protocol::xproto::ConnectionExt as _;
    if let Ok((conn, screen)) = x11rb::connect(None) {
        let root = conn.setup().roots[screen].root;
        let _ = conn.warp_pointer(x11rb::NONE, root, 0, 0, 0, 0, x as i16, y as i16);
        let _ = conn.flush();
    }
}

/// Whether the left mouse button is held right now.
#[cfg(target_os = "linux")]
pub fn left_button_held() -> bool {
    use x11rb::connection::Connection;
    use x11rb::protocol::xproto::{ConnectionExt as _, KeyButMask};
    let Ok((conn, screen)) = x11rb::connect(None) else { return false };
    let root = conn.setup().roots[screen].root;
    conn.query_pointer(root).ok().and_then(|c| c.reply().ok()).is_some_and(|p| p.mask.contains(KeyButMask::BUTTON1))
}

/// Ends a drag on this computer by releasing the left button (Async's own release, through XTest).
#[cfg(target_os = "linux")]
pub fn release_left() {
    use x11rb::connection::Connection;
    use x11rb::protocol::xtest::ConnectionExt as _;
    if let Ok((conn, screen)) = x11rb::connect(None) {
        let root = conn.setup().roots[screen].root;
        let _ = conn.xtest_fake_input(5, 1, 0, root, 0, 0, 0);
        let _ = conn.flush();
    }
}

// ---------- macOS ----------

#[cfg(target_os = "macos")]
pub fn start(center: (i32, i32), on: impl Fn(Captured) + Send + 'static) -> Result<Capture> {
    use core_foundation::base::TCFType;
    use core_foundation::runloop::{CFRunLoop, kCFRunLoopCommonModes};
    use core_graphics::event::{CGEventFlags, CGEventTap, CGEventTapLocation, CGEventTapOptions, CGEventTapPlacement, CGEventType, EventField};

    let stop = Arc::new(AtomicBool::new(false));
    let run_loop = Arc::new(std::sync::Mutex::new(None::<usize>));
    let (ready_tx, ready_rx) = std::sync::mpsc::channel::<Result<(), String>>();
    let (stopping, rl) = (stop.clone(), run_loop.clone());
    let thread = std::thread::Builder::new().name("capture".into()).spawn(move || {
        let keys = std::cell::RefCell::new(Keys::default());
        let caps = std::cell::Cell::new(false);
        let kinds = vec![
            CGEventType::MouseMoved,
            CGEventType::LeftMouseDown,
            CGEventType::LeftMouseUp,
            CGEventType::RightMouseDown,
            CGEventType::RightMouseUp,
            CGEventType::OtherMouseDown,
            CGEventType::OtherMouseUp,
            CGEventType::LeftMouseDragged,
            CGEventType::RightMouseDragged,
            CGEventType::OtherMouseDragged,
            CGEventType::ScrollWheel,
            CGEventType::KeyDown,
            CGEventType::KeyUp,
            CGEventType::FlagsChanged,
        ];
        let tap = CGEventTap::new(CGEventTapLocation::HID, CGEventTapPlacement::HeadInsertEventTap, CGEventTapOptions::Default, kinds, |_proxy, kind, event| {
            let int = |f| event.get_integer_value_field(f);
            match kind {
                CGEventType::MouseMoved | CGEventType::LeftMouseDragged | CGEventType::RightMouseDragged | CGEventType::OtherMouseDragged => {
                    let (dx, dy) = (int(EventField::MOUSE_EVENT_DELTA_X) as i32, int(EventField::MOUSE_EVENT_DELTA_Y) as i32);
                    if dx != 0 || dy != 0 {
                        on(Captured::Move(dx, dy));
                    }
                }
                CGEventType::LeftMouseDown => on(Captured::Button(1, true)),
                CGEventType::LeftMouseUp => on(Captured::Button(1, false)),
                CGEventType::RightMouseDown => on(Captured::Button(3, true)),
                CGEventType::RightMouseUp => on(Captured::Button(3, false)),
                CGEventType::OtherMouseDown | CGEventType::OtherMouseUp => {
                    let down = matches!(kind, CGEventType::OtherMouseDown);
                    match int(EventField::MOUSE_EVENT_BUTTON_NUMBER) {
                        2 => on(Captured::Button(2, down)),
                        3 => on(Captured::Button(4, down)),
                        4 => on(Captured::Button(5, down)),
                        _ => {}
                    }
                }
                CGEventType::ScrollWheel => {
                    let lines = |f| event.get_double_value_field(f);
                    let dy = (lines(EventField::SCROLL_WHEEL_EVENT_FIXED_POINT_DELTA_AXIS_1) * 40.0) as i16;
                    let dx = (-lines(EventField::SCROLL_WHEEL_EVENT_FIXED_POINT_DELTA_AXIS_2) * 40.0) as i16;
                    if dy != 0 || dx != 0 {
                        on(Captured::Wheel(dy, dx));
                    }
                }
                CGEventType::KeyDown | CGEventType::KeyUp => {
                    let down = matches!(kind, CGEventType::KeyDown);
                    if down && int(EventField::KEYBOARD_EVENT_AUTOREPEAT) != 0 {
                        // Key repeat comes from the other computer's own settings.
                        return None;
                    }
                    if let Some(code) = crate::share::evdev_from_mac(int(EventField::KEYBOARD_EVENT_KEYCODE) as u16) {
                        if keys.borrow_mut().press(code, down) {
                            on(Captured::Recall);
                        } else {
                            on(Captured::Key(code, down));
                        }
                    }
                }
                CGEventType::FlagsChanged => {
                    let mac = int(EventField::KEYBOARD_EVENT_KEYCODE) as u16;
                    let flags = event.get_flags();
                    let Some(code) = crate::share::evdev_from_mac(mac) else { return None };
                    if mac == 0x39 {
                        // Caps Lock reports its new state, not presses: tap it over there when it flips.
                        let on_now = flags.contains(CGEventFlags::CGEventFlagAlphaShift);
                        if on_now != caps.get() {
                            caps.set(on_now);
                            on(Captured::Key(code, true));
                            on(Captured::Key(code, false));
                        }
                        return None;
                    }
                    let flag = match mac {
                        0x37 | 0x36 => CGEventFlags::CGEventFlagCommand,
                        0x3B | 0x3E => CGEventFlags::CGEventFlagControl,
                        0x3A | 0x3D => CGEventFlags::CGEventFlagAlternate,
                        0x38 | 0x3C => CGEventFlags::CGEventFlagShift,
                        _ => return None,
                    };
                    let down = flags.contains(flag);
                    keys.borrow_mut().press(code, down);
                    on(Captured::Key(code, down));
                }
                _ => return Some(event.clone()),
            }
            None // taken: nothing happens on this Mac
        });
        let tap = match tap {
            Ok(t) => t,
            Err(()) => {
                let _ = ready_tx.send(Err("macOS didn't allow it: turn on async-desktop in System Settings > Privacy & Security > Accessibility".into()));
                return;
            }
        };
        unsafe {
            let Ok(source) = tap.mach_port.create_runloop_source(0) else {
                let _ = ready_tx.send(Err("couldn't listen to the mouse".into()));
                return;
            };
            let current = CFRunLoop::get_current();
            *rl.lock().unwrap() = Some(current.as_concrete_TypeRef() as usize);
            current.add_source(&source, kCFRunLoopCommonModes);
            tap.enable();
            let _ = ready_tx.send(Ok(()));
            if !stopping.load(Ordering::Relaxed) {
                CFRunLoop::run_current();
            }
        }
    })?;
    match ready_rx.recv() {
        Ok(Ok(())) => {}
        Ok(Err(why)) => anyhow::bail!(why),
        Err(_) => anyhow::bail!("couldn't start capturing the mouse"),
    }
    mac::hide(center);
    Ok(Capture { stop, run_loop, thread: Some(thread) })
}

#[cfg(target_os = "macos")]
mod mac {
    use core_graphics::display::CGDisplay;
    use core_graphics::geometry::CGPoint;

    unsafe extern "C" {
        fn CGAssociateMouseAndMouseCursorPosition(connected: u32) -> i32;
        fn CGWarpMouseCursorPosition(point: CGPoint) -> i32;
    }

    /// Hides the cursor and detaches it from the mouse (which keeps reporting movement).
    pub fn hide(center: (i32, i32)) {
        unsafe {
            CGWarpMouseCursorPosition(CGPoint::new(center.0 as f64, center.1 as f64));
            CGAssociateMouseAndMouseCursorPosition(0);
        }
        let _ = CGDisplay::main().hide_cursor();
    }

    pub fn show_at(x: i32, y: i32) {
        unsafe {
            CGWarpMouseCursorPosition(CGPoint::new(x as f64, y as f64));
            CGAssociateMouseAndMouseCursorPosition(1);
        }
        let _ = CGDisplay::main().show_cursor();
    }
}

/// Puts this computer's pointer at a desktop point (where it comes back) and shows it again.
#[cfg(target_os = "macos")]
pub fn warp(x: i32, y: i32) {
    mac::show_at(x, y);
}

#[cfg(target_os = "macos")]
pub fn left_button_held() -> bool {
    unsafe extern "C" {
        fn CGEventSourceButtonState(state: i32, button: u32) -> bool;
    }
    // kCGEventSourceStateCombinedSessionState, kCGMouseButtonLeft
    unsafe { CGEventSourceButtonState(0, 0) }
}

/// Ends a drag on this Mac by releasing the left button where the pointer is.
#[cfg(target_os = "macos")]
pub fn release_left() {
    use core_graphics::event::{CGEvent, CGEventTapLocation, CGEventType, CGMouseButton};
    use core_graphics::event_source::{CGEventSource, CGEventSourceStateID};
    let Ok(source) = CGEventSource::new(CGEventSourceStateID::HIDSystemState) else { return };
    let at = CGEvent::new(source.clone()).map(|e| e.location()).unwrap_or_default();
    if let Ok(e) = CGEvent::new_mouse_event(source, CGEventType::LeftMouseUp, at, CGMouseButton::Left) {
        e.post(CGEventTapLocation::HID);
    }
}

// ---------- elsewhere (development builds of the receiver on Windows) ----------

#[cfg(not(any(target_os = "linux", target_os = "macos")))]
pub fn start(_center: (i32, i32), _on: impl Fn(Captured) + Send + 'static) -> Result<Capture> {
    anyhow::bail!("not on this system")
}
#[cfg(not(any(target_os = "linux", target_os = "macos")))]
pub fn warp(_x: i32, _y: i32) {}
#[cfg(not(any(target_os = "linux", target_os = "macos")))]
pub fn left_button_held() -> bool {
    false
}
#[cfg(not(any(target_os = "linux", target_os = "macos")))]
pub fn release_left() {}
