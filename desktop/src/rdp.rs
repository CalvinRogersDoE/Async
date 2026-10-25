//! A Remote Desktop session into a paired Windows (or Linux) computer, through the tunnel, using
//! IronRDP's client engine. Input forwarding follows IronRDP's own viewer
//! (crates/ironrdp-viewer/src/app.rs, Apache-2.0 OR MIT).

use anyhow::Result;
use ironrdp::client::config::{ConfigBuilder, Destination};
use ironrdp::client::output_channel::output_channel;
use ironrdp::client::rdp::{RdpClient, RdpInputEvent, RdpInputSender, RdpOutputEvent};
use ironrdp::input::{Database, MouseButton, MousePosition, Operation, Scancode, WheelRotations};
use ironrdp::pdu::gcc::{ClientMonitorData, Monitor, MonitorFlags};
use winit::event::{ElementState, KeyEvent, MouseScrollDelta, WindowEvent};
use winit::keyboard::PhysicalKey;

use crate::core::RemoteScreen;
use crate::keymap;

/// What the session tells the app.
#[derive(Debug)]
pub enum Out {
    Frame { pixels: Vec<u32>, width: u16, height: u16 },
    LoggedIn,
    Ended(String),
}

pub struct Session {
    input: RdpInputSender,
    db: Database,
}

impl Session {
    /// Starts a session at `size`, or, when the computer has several screens, with its own screen
    /// layout (so its windows stay where they are); the frames then cover all of its screens.
    pub fn start(rt: &tokio::runtime::Handle, host: &str, port: u16, user: &str, password: &str, size: (u16, u16), screens: &[RemoteScreen], report: impl Fn(Out) + Send + 'static) -> Result<Self> {
        let mut config = ConfigBuilder::new()
            .with_destination(Destination::new(format!("{host}:{port}"))?)
            .with_username(user)
            .with_password(password)
            .with_desktop_scale_factor(100)
            .with_autologon(true);
        if screens.len() > 1 {
            let (x0, y0, x1, y1) = bounds(screens);
            // Main screen first, as Windows' own Remote Desktop client does.
            let mut ordered = screens.to_vec();
            ordered.sort_by_key(|s| !s.primary);
            let monitors = ordered
                .iter()
                .map(|s| Monitor { left: s.x, top: s.y, right: s.x + s.w - 1, bottom: s.y + s.h - 1, flags: if s.primary { MonitorFlags::PRIMARY } else { MonitorFlags::empty() } })
                .collect();
            config = config.with_desktop_width((x1 - x0) as u16).with_desktop_height((y1 - y0) as u16).with_monitor_layout(ClientMonitorData { monitors });
        } else {
            config = config.with_desktop_width(size.0).with_desktop_height(size.1);
        }
        let config = config.build()?;
        let (sender, mut receiver) = output_channel(64);
        let client = RdpClient::new(config, sender);
        let input = client.input_sender();
        rt.spawn(async move {
            while let Some(event) = receiver.recv().await {
                match event {
                    RdpOutputEvent::Image { buffer, width, height } => report(Out::Frame { pixels: buffer, width: width.get(), height: height.get() }),
                    RdpOutputEvent::LoginComplete => report(Out::LoggedIn),
                    RdpOutputEvent::ConnectionFailure(e) => report(Out::Ended(format!("Couldn't connect: {e}"))),
                    RdpOutputEvent::Terminated(Ok(reason)) => report(Out::Ended(format!("The session ended ({reason:?})"))),
                    RdpOutputEvent::Terminated(Err(e)) => report(Out::Ended(format!("The session ended: {e}"))),
                    _ => {}
                }
            }
        });
        // The session future isn't Send, so it gets a thread of its own (as in IronRDP's viewer).
        let handle = rt.clone();
        std::thread::Builder::new().name("rdp-session".into()).spawn(move || handle.block_on(client.run()))?;
        Ok(Self { input, db: Database::new() })
    }

    pub fn close(&mut self) {
        self.release_all();
        self.input.request_close();
    }

    fn send(&mut self, operations: impl IntoIterator<Item = Operation>) {
        let Ok(permit) = self.input.try_reserve() else { return };
        let events = self.db.apply(operations);
        if !events.is_empty() {
            permit.send(RdpInputEvent::FastPath(events));
        }
    }

    pub fn release_all(&mut self) {
        let Ok(permit) = self.input.try_reserve() else { return };
        let events = self.db.release_all();
        if !events.is_empty() {
            permit.send(RdpInputEvent::FastPath(events));
        }
    }

    pub fn mouse_to(&mut self, x: f64, y: f64) {
        let pos = MousePosition { x: x.max(0.0) as u16, y: y.max(0.0) as u16 };
        self.send([Operation::MouseMove(pos)]);
    }

    /// Forwards keyboard, mouse and wheel input from the portal window; `to_desktop` maps a window
    /// position to the session's desktop, or None outside the picture.
    pub fn window_input(&mut self, event: &WindowEvent, to_desktop: impl Fn(f64, f64) -> Option<(f64, f64)>) {
        match event {
            WindowEvent::KeyboardInput { event, .. } => {
                if let Some(ops) = key_operations(event) {
                    self.send(ops);
                }
            }
            WindowEvent::ModifiersChanged(modifiers) => {
                let s = modifiers.state();
                let ops = [
                    (s.shift_key(), Scancode::from_u8(false, 0x2A)),
                    (s.control_key(), Scancode::from_u8(false, 0x1D)),
                    (s.alt_key(), Scancode::from_u8(false, 0x38)),
                    (s.super_key(), Scancode::from_u8(true, 0x5B)),
                ]
                .map(|(down, code)| if down { Operation::KeyPressed(code) } else { Operation::KeyReleased(code) });
                self.send(ops);
            }
            WindowEvent::CursorMoved { position, .. } => {
                if let Some((x, y)) = to_desktop(position.x, position.y) {
                    self.mouse_to(x, y);
                }
            }
            WindowEvent::MouseWheel { delta, .. } => {
                let (x, y) = match *delta {
                    MouseScrollDelta::LineDelta(x, y) => (x as f64 * 120.0, y as f64 * 120.0),
                    MouseScrollDelta::PixelDelta(p) => (p.x, p.y),
                };
                let ops = [(false, x), (true, y)]
                    .into_iter()
                    .filter(|(_, v)| v.abs() > 0.001)
                    .map(|(is_vertical, v)| Operation::WheelRotations(WheelRotations { is_vertical, rotation_units: v as i16 }));
                self.send(ops.collect::<Vec<_>>());
            }
            WindowEvent::MouseInput { state, button, .. } => {
                let b = match button {
                    winit::event::MouseButton::Left => MouseButton::Left,
                    winit::event::MouseButton::Right => MouseButton::Right,
                    winit::event::MouseButton::Middle => MouseButton::Middle,
                    winit::event::MouseButton::Back => MouseButton::X1,
                    winit::event::MouseButton::Forward => MouseButton::X2,
                    winit::event::MouseButton::Other(_) => return,
                };
                self.send([match state {
                    ElementState::Pressed => Operation::MouseButtonPressed(b),
                    ElementState::Released => Operation::MouseButtonReleased(b),
                }]);
            }
            WindowEvent::Focused(false) => self.release_all(),
            _ => {}
        }
    }
}

fn key_operations(event: &KeyEvent) -> Option<Vec<Operation>> {
    let PhysicalKey::Code(code) = event.physical_key else { return None };
    // ModifiersChanged reports these.
    if keymap::is_modifier(code) {
        return None;
    }
    let (scancode, release_only) = keymap::map_key_code(code)?;
    Some(match event.state {
        ElementState::Pressed => vec![Operation::KeyPressed(scancode)],
        ElementState::Released if release_only => vec![Operation::KeyPressed(scancode), Operation::KeyReleased(scancode)],
        ElementState::Released => vec![Operation::KeyReleased(scancode)],
    })
}

/// The rectangle around all the screens: (left, top, right, bottom), right and bottom exclusive.
pub fn bounds(screens: &[RemoteScreen]) -> (i32, i32, i32, i32) {
    let x0 = screens.iter().map(|s| s.x).min().unwrap_or(0);
    let y0 = screens.iter().map(|s| s.y).min().unwrap_or(0);
    let x1 = screens.iter().map(|s| s.x + s.w).max().unwrap_or(1);
    let y1 = screens.iter().map(|s| s.y + s.h).max().unwrap_or(1);
    (x0, y0, x1, y1)
}
