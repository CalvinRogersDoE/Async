//! The tray icon: a StatusNotifierItem on Linux (Cinnamon, KDE, XFCE, GNOME with AppIndicator), a
//! menu-bar item on macOS. Both send the same commands.

#[derive(Debug, Clone, Copy)]
pub enum MenuCmd {
    PunchHere,
    Close,
    Disconnect,
    NextScreen,
    PrevScreen,
    Settings,
    Quit,
}

const ITEMS: [(&str, Option<MenuCmd>); 9] = [
    ("Punch in here", Some(MenuCmd::PunchHere)),
    ("Close portal", Some(MenuCmd::Close)),
    ("Next screen of the other computer", Some(MenuCmd::NextScreen)),
    ("Previous screen", Some(MenuCmd::PrevScreen)),
    ("Disconnect", Some(MenuCmd::Disconnect)),
    ("", None),
    ("Settings…", Some(MenuCmd::Settings)),
    ("", None),
    ("Quit Async", Some(MenuCmd::Quit)),
];

/// Keeps the tray icon alive; drop it to remove the icon.
pub type Handle = Box<dyn std::any::Any>;

/// Async's purple ring, as straight RGBA rows.
fn ring_rgba(size: u32) -> Vec<u8> {
    let mut data = Vec::with_capacity((size * size * 4) as usize);
    let c = size as f32 / 2.0;
    let (outer, inner) = (c - 1.0, c * 0.52);
    for y in 0..size {
        for x in 0..size {
            let d = ((x as f32 + 0.5 - c).powi(2) + (y as f32 + 0.5 - c).powi(2)).sqrt();
            let a = ((outer - d).min(d - inner) + 0.5).clamp(0.0, 1.0);
            let glow = 1.0 - ((d - inner) / (outer - inner)).clamp(0.0, 1.0);
            let (r, g, b) = (176.0 + 79.0 * glow * 0.5, 92.0 + 100.0 * glow * 0.4, 255.0);
            data.extend_from_slice(&[r as u8, g as u8, b as u8, (a * 255.0) as u8]);
        }
    }
    data
}

#[cfg(target_os = "linux")]
pub fn spawn(send: impl Fn(MenuCmd) + Send + Sync + 'static) -> Option<Handle> {
    use ksni::blocking::TrayMethods as _;
    use std::sync::Arc;

    struct AsyncTray {
        send: Arc<dyn Fn(MenuCmd) + Send + Sync>,
    }

    impl ksni::Tray for AsyncTray {
        fn id(&self) -> String {
            "async".into()
        }
        fn title(&self) -> String {
            "Async".into()
        }
        fn icon_pixmap(&self) -> Vec<ksni::Icon> {
            // ksni wants ARGB.
            [22, 32, 48]
                .into_iter()
                .map(|size| {
                    let data = ring_rgba(size).chunks(4).flat_map(|p| [p[3], p[0], p[1], p[2]]).collect();
                    ksni::Icon { width: size as i32, height: size as i32, data }
                })
                .collect()
        }
        fn tool_tip(&self) -> ksni::ToolTip {
            ksni::ToolTip { title: "Async".into(), description: "Push the mouse through a screen edge to punch into another computer".into(), ..Default::default() }
        }
        fn activate(&mut self, _x: i32, _y: i32) {
            (self.send)(MenuCmd::Settings);
        }
        fn menu(&self) -> Vec<ksni::MenuItem<Self>> {
            ITEMS
                .iter()
                .map(|&(label, cmd)| match cmd {
                    None => ksni::MenuItem::Separator,
                    Some(cmd) => ksni::menu::StandardItem { label: label.into(), activate: Box::new(move |t: &mut Self| (t.send)(cmd)), ..Default::default() }.into(),
                })
                .collect()
        }
    }

    match (AsyncTray { send: Arc::new(send) }).spawn() {
        Ok(handle) => Some(Box::new(handle)),
        Err(e) => {
            eprintln!("No tray icon ({e}); use the Async entry in your applications menu for Settings.");
            None
        }
    }
}

/// macOS: must be called on the main thread once the event loop is running.
#[cfg(target_os = "macos")]
pub fn spawn(send: impl Fn(MenuCmd) + Send + Sync + 'static) -> Option<Handle> {
    use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};
    use tray_icon::{Icon, TrayIconBuilder};

    let menu = Menu::new();
    let mut ids = Vec::new();
    for &(label, cmd) in &ITEMS {
        match cmd {
            None => {
                let _ = menu.append(&PredefinedMenuItem::separator());
            }
            Some(cmd) => {
                let item = MenuItem::new(label, true, None);
                ids.push((item.id().clone(), cmd));
                let _ = menu.append(&item);
            }
        }
    }
    MenuEvent::set_event_handler(Some(move |event: MenuEvent| {
        if let Some(&(_, cmd)) = ids.iter().find(|(id, _)| *id == event.id) {
            send(cmd);
        }
    }));
    let icon = Icon::from_rgba(ring_rgba(36), 36, 36).ok()?;
    match TrayIconBuilder::new().with_menu(Box::new(menu)).with_icon(icon).with_tooltip("Async").build() {
        Ok(tray) => Some(Box::new(tray)),
        Err(e) => {
            eprintln!("No menu-bar icon ({e})");
            None
        }
    }
}
