//! Settings, shared by the running app and the settings window through
//! ~/.config/async/desktop.json. Passwords never go here: they live in the desktop keyring.

use std::collections::HashMap;
use std::path::PathBuf;
use std::time::SystemTime;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum Edge {
    Top,
    Bottom,
    Left,
    Right,
}

impl Edge {
    /// The edges that can lead to a computer; the bottom is always the way back.
    pub const MAPPABLE: [Edge; 3] = [Edge::Top, Edge::Left, Edge::Right];

    pub fn horizontal(self) -> bool {
        matches!(self, Edge::Top | Edge::Bottom)
    }

    pub fn name(self) -> &'static str {
        match self {
            Edge::Top => "top",
            Edge::Bottom => "bottom",
            Edge::Left => "left",
            Edge::Right => "right",
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct Settings {
    /// Which paired device each monitor edge opens, keyed "<monitor>/<Edge>". None until set in
    /// Settings, which means every free top edge opens the first paired device.
    pub edges: Option<HashMap<String, String>>,
    /// What each mapped edge does, keyed like `edges`: "portal" (the other computer's screen opens
    /// here) or "mouse" (the pointer and keyboard move over to it, like a monitor beside this one).
    /// Missing means portal.
    pub modes: HashMap<String, String>,
    /// How hard to push through a mouse-mode edge: lighter than a portal.
    pub mouse_push_distance: u32,
    /// The user to sign in as on each device (Remote Desktop), by device id.
    pub users: HashMap<String, String>,
    /// How hard to push against the edge, in raw mouse counts.
    pub push_distance: u32,
    /// A pause longer than this resets the push.
    pub push_gap_ms: u64,
    /// The push has to last at least this long, so a fast flick past the edge doesn't count.
    pub push_hold_ms: u64,
    pub color_in: [u8; 3],
    pub color_out: [u8; 3],
    pub open_ms: u64,
    pub close_ms: u64,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            edges: None,
            modes: HashMap::new(),
            mouse_push_distance: 120,
            users: HashMap::new(),
            push_distance: 700,
            push_gap_ms: 300,
            push_hold_ms: 250,
            color_in: [176, 92, 255],
            color_out: [255, 154, 46],
            open_ms: 700,
            close_ms: 480,
        }
    }
}

/// Edge targets are a device id, or "id@N" for screen N of a device with several.
pub fn split_target(v: &str) -> (&str, Option<usize>) {
    match v.split_once('@') {
        Some((d, n)) => (d, n.parse().ok()),
        None => (v, None),
    }
}

pub fn edge_key(monitor: &str, e: Edge) -> String {
    format!("{monitor}/{e:?}")
}

/// Async's own folder: ~/.config/async on Linux, ~/Library/Application Support/Async on macOS
/// (the same places install.sh and async-core use).
pub fn folder() -> PathBuf {
    let home = std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default();
    if cfg!(target_os = "macos") {
        return home.join("Library/Application Support/Async");
    }
    std::env::var_os("XDG_CONFIG_HOME").map(PathBuf::from).unwrap_or_else(|| home.join(".config")).join("async")
}

fn path() -> PathBuf {
    folder().join("desktop.json")
}

impl Settings {
    pub fn load() -> Self {
        std::fs::read(path()).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or_default()
    }

    pub fn save(&self) -> std::io::Result<()> {
        std::fs::create_dir_all(folder())?;
        let tmp = path().with_extension("json.tmp");
        std::fs::write(&tmp, serde_json::to_vec_pretty(self)?)?;
        std::fs::rename(tmp, path())
    }

    pub fn modified() -> Option<SystemTime> {
        std::fs::metadata(path()).and_then(|m| m.modified()).ok()
    }

    /// The device pushing through this edge opens, if any, and which of its screens (by position,
    /// see `core::sorted`) when it has several.
    pub fn device_for(&self, monitor: &str, e: Edge, first_device: Option<&str>) -> Option<(String, Option<usize>)> {
        // The bottom is always the way back.
        if e == Edge::Bottom {
            return None;
        }
        match &self.edges {
            None => (e == Edge::Top).then(|| first_device.map(|d| (d.to_string(), None))).flatten(),
            Some(map) => map.get(&edge_key(monitor, e)).filter(|d| !d.is_empty()).map(|v| {
                let (d, screen) = split_target(v);
                (d.to_string(), screen)
            }),
        }
    }

    pub fn is_mouse_edge(&self, monitor: &str, e: Edge) -> bool {
        e != Edge::Bottom && self.modes.get(&edge_key(monitor, e)).is_some_and(|m| m == "mouse")
    }

    pub fn user_for(&self, device: &str) -> String {
        self.users.get(device).cloned().unwrap_or_default()
    }
}

/// Screen sharing passwords, in the desktop's keyring (GNOME Keyring, KWallet…) under "Async".
pub mod secrets {
    fn entry(device: &str) -> keyring::Result<keyring::Entry> {
        keyring::Entry::new("Async", device)
    }

    // The keyring library runs its own async runtime, so these must be called off any tokio thread.
    pub fn get(device: &str) -> Option<String> {
        entry(device).and_then(|e| e.get_password()).ok()
    }

    pub fn set(device: &str, password: &str) -> keyring::Result<()> {
        entry(device)?.set_password(password)
    }

    pub fn delete(device: &str) {
        if let Ok(e) = entry(device) {
            let _ = e.delete_credential();
        }
    }
}
