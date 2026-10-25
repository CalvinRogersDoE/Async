//! The tray icon (StatusNotifierItem, shown by Cinnamon, KDE, XFCE and GNOME with AppIndicator).

use ksni::blocking::TrayMethods as _;
use winit::event_loop::EventLoopProxy;

use crate::app::{AppEvent, MenuCmd};

struct AsyncTray {
    proxy: EventLoopProxy<AppEvent>,
}

impl AsyncTray {
    fn send(&self, cmd: MenuCmd) {
        let _ = self.proxy.send_event(AppEvent::Menu(cmd));
    }
}

impl ksni::Tray for AsyncTray {
    fn id(&self) -> String {
        "async".into()
    }

    fn title(&self) -> String {
        "Async".into()
    }

    fn icon_pixmap(&self) -> Vec<ksni::Icon> {
        [22, 32, 48].into_iter().map(ring_icon).collect()
    }

    fn tool_tip(&self) -> ksni::ToolTip {
        ksni::ToolTip { title: "Async".into(), description: "Push the mouse through a screen edge to punch into another computer".into(), ..Default::default() }
    }

    fn activate(&mut self, _x: i32, _y: i32) {
        self.send(MenuCmd::Settings);
    }

    fn menu(&self) -> Vec<ksni::MenuItem<Self>> {
        use ksni::menu::StandardItem;
        let item = |label: &str, cmd: MenuCmd| -> ksni::MenuItem<Self> {
            StandardItem { label: label.into(), activate: Box::new(move |t: &mut Self| t.send(cmd)), ..Default::default() }.into()
        };
        vec![
            item("Punch in here", MenuCmd::PunchHere),
            item("Close portal", MenuCmd::Close),
            item("Next screen of the other computer", MenuCmd::NextScreen),
            item("Previous screen", MenuCmd::PrevScreen),
            item("Disconnect", MenuCmd::Disconnect),
            ksni::MenuItem::Separator,
            item("Settings…", MenuCmd::Settings),
            ksni::MenuItem::Separator,
            item("Quit Async", MenuCmd::Quit),
        ]
    }
}

pub fn spawn(proxy: EventLoopProxy<AppEvent>) {
    if let Err(e) = (AsyncTray { proxy }).spawn() {
        eprintln!("No tray icon ({e}); use the Async entry in your applications menu for Settings.");
    }
}

/// Async's purple ring, as ARGB32 in network byte order.
fn ring_icon(size: i32) -> ksni::Icon {
    let mut data = Vec::with_capacity((size * size * 4) as usize);
    let c = size as f32 / 2.0;
    let (outer, inner) = (c - 1.0, c * 0.52);
    for y in 0..size {
        for x in 0..size {
            let d = ((x as f32 + 0.5 - c).powi(2) + (y as f32 + 0.5 - c).powi(2)).sqrt();
            let a = ((outer - d).min(d - inner) + 0.5).clamp(0.0, 1.0);
            let glow = 1.0 - ((d - inner) / (outer - inner)).clamp(0.0, 1.0);
            let (r, g, b) = (176.0 + 79.0 * glow * 0.5, 92.0 + 100.0 * glow * 0.4, 255.0);
            data.extend_from_slice(&[(a * 255.0) as u8, r as u8, g as u8, b as u8]);
        }
    }
    ksni::Icon { width: size, height: size, data }
}
