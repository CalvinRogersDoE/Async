//! The settings window (`async-desktop settings`, or the tray icon): paired devices and their
//! sign-ins, pairing, which screen edge opens which computer, and how hard to push.

use std::collections::HashMap;
use std::sync::mpsc::{Receiver, Sender, channel};
use std::sync::Arc;
use std::time::{Duration, Instant};

use eframe::egui::{self, Color32, CornerRadius, Pos2, Rect, RichText, Sense, Stroke, Vec2};
use serde_json::Value;

use crate::config::{Edge, Settings, edge_key, secrets, split_target};
use crate::core::{self as async_core, Core, Device, RemoteScreen};
use crate::monitors::{self, Monitor};

const ACCENT: Color32 = Color32::from_rgb(176, 92, 255);
const DIM: Color32 = Color32::from_rgb(160, 150, 190);
const MONITOR: Color32 = Color32::from_rgb(38, 34, 58);
const UNSET: Color32 = Color32::from_rgb(70, 64, 96);
const PALETTE: [Color32; 6] = [
    Color32::from_rgb(176, 92, 255),
    Color32::from_rgb(46, 204, 170),
    Color32::from_rgb(255, 154, 46),
    Color32::from_rgb(255, 92, 160),
    Color32::from_rgb(90, 160, 255),
    Color32::from_rgb(240, 210, 70),
];

enum Msg {
    Devices(Vec<Device>),
    Described(String, Result<(String, Vec<String>, Vec<RemoteScreen>), String>),
    Code(Result<(String, u64), String>),
    Paired(String),
    PairEnded(String),
    Joined(Result<String, String>),
    Info(String),
}

struct Row {
    device: Device,
    label: String,
    user: String,
    password: String,
    vnc_only: bool,
    /// Its screens in desk order, when it has several (Windows).
    screens: Vec<RemoteScreen>,
}

struct SettingsApp {
    core: Option<Arc<Core>>,
    error: Option<String>,
    cfg: Settings,
    rows: Vec<Row>,
    edges: HashMap<String, String>,
    monitors: Vec<Monitor>,
    picked: Option<(String, Edge)>,
    code: Option<(String, Instant)>,
    join_code: String,
    status: String,
    tx: Sender<Msg>,
    rx: Receiver<Msg>,
}

pub fn run() -> anyhow::Result<()> {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default().with_title("Async settings").with_inner_size([560.0, 760.0]).with_app_id("async"),
        ..Default::default()
    };
    eframe::run_native("Async settings", options, Box::new(|cc| Ok(Box::new(SettingsApp::new(cc)))))
        .map_err(|e| anyhow::anyhow!("{e}"))
}

impl SettingsApp {
    fn new(cc: &eframe::CreationContext) -> Self {
        let mut visuals = egui::Visuals::dark();
        visuals.panel_fill = Color32::from_rgb(18, 16, 28);
        visuals.selection.bg_fill = ACCENT;
        cc.egui_ctx.set_visuals(visuals);

        let (tx, rx) = channel();
        let events = tx.clone();
        let ctx = cc.egui_ctx.clone();
        let core = Core::connect(move |ev: Value| {
            let text = |k: &str| ev.get(k).and_then(Value::as_str).unwrap_or_default().to_string();
            let msg = match ev.get("event").and_then(Value::as_str) {
                Some("paired") => Msg::Paired(ev["peer"]["name"].as_str().unwrap_or("a device").to_string()),
                Some("pair_ended") => Msg::PairEnded(text("error")),
                _ => return,
            };
            let _ = events.send(msg);
            ctx.request_repaint();
        });
        let cfg = Settings::load();
        let monitors = monitors::list_now();
        let (core, error) = match core {
            Ok(c) => (Some(c), None),
            Err(e) => (None, Some(format!("{e:#}"))),
        };
        let app = Self {
            core,
            error,
            edges: cfg.edges.clone().unwrap_or_default(),
            cfg,
            rows: Vec::new(),
            monitors,
            picked: None,
            code: None,
            join_code: String::new(),
            status: String::new(),
            tx,
            rx,
        };
        app.load_devices(&cc.egui_ctx);
        app
    }

    fn load_devices(&self, ctx: &egui::Context) {
        let Some(core) = self.core.clone() else { return };
        let (tx, ctx) = (self.tx.clone(), ctx.clone());
        std::thread::spawn(move || {
            let devices = core.peers().unwrap_or_default();
            let _ = tx.send(Msg::Devices(devices.clone()));
            ctx.request_repaint();
            for d in devices {
                let r = core.describe(&d.id).map(|i| (i.os, i.services, async_core::sorted(i.screens))).map_err(|e| format!("{e:#}"));
                let _ = tx.send(Msg::Described(d.id, r));
                ctx.request_repaint();
            }
        });
    }

    fn handle(&mut self, msg: Msg, ctx: &egui::Context) {
        match msg {
            Msg::Devices(list) => {
                let first = list.first().map(|d| d.id.clone());
                self.rows = list
                    .into_iter()
                    .map(|d| Row { label: d.name.clone(), user: self.cfg.user_for(&d.id), password: String::new(), vnc_only: false, screens: Vec::new(), device: d })
                    .collect();
                // Never set up: start from every free top edge opening the first paired device.
                if self.cfg.edges.is_none() && self.edges.is_empty() {
                    if let Some(first) = first {
                        for m in &self.monitors {
                            if monitors::is_free(&self.monitors, m, Edge::Top) {
                                self.edges.insert(edge_key(&m.name, Edge::Top), first.clone());
                            }
                        }
                    }
                }
            }
            Msg::Described(id, r) => {
                if let Some(row) = self.rows.iter_mut().find(|r| r.device.id == id) {
                    row.label = match r {
                        Ok((os, services, screens)) => {
                            row.screens = screens;
                            let os = match os.as_str() {
                                "windows" => "Windows",
                                "macos" => "macOS",
                                "linux" => "Linux",
                                o => o,
                            };
                            row.vnc_only = !services.iter().any(|s| s == "rdp") && services.iter().any(|s| s == "vnc");
                            if services.is_empty() { format!("{} · {os} · screen sharing off", row.device.name) } else { format!("{} · {os}", row.device.name) }
                        }
                        Err(_) => format!("{} · offline", row.device.name),
                    };
                }
            }
            Msg::Code(Ok((code, secs))) => self.code = Some((code, Instant::now() + Duration::from_secs(secs))),
            Msg::Code(Err(e)) | Msg::PairEnded(e) => {
                self.code = None;
                self.status = e;
            }
            Msg::Paired(name) => {
                self.code = None;
                self.status = format!("Paired with {name}.");
                self.load_devices(ctx);
            }
            Msg::Joined(Ok(name)) => {
                self.join_code.clear();
                self.status = format!("Paired with {name}.");
                self.load_devices(ctx);
            }
            Msg::Joined(Err(e)) => self.status = e,
            Msg::Info(s) => self.status = s,
        }
    }

    fn color_of(&self, target: &str) -> Color32 {
        let id = split_target(target).0;
        self.rows.iter().position(|r| r.device.id == id).map(|i| PALETTE[i % PALETTE.len()]).unwrap_or(UNSET)
    }

    /// "SS-BNYE", or "SS-BNYE · Middle screen (2560×1440, main)" for one screen of it.
    fn name_of(&self, target: &str) -> String {
        let (id, screen) = split_target(target);
        let Some(row) = self.rows.iter().find(|r| r.device.id == id) else { return "a removed device".into() };
        match screen.and_then(|n| row.screens.get(n).map(|s| (n, s))) {
            Some((n, s)) => format!("{} · {}", row.device.name, async_core::screen_label(s, n, row.screens.len())),
            None => row.device.name.clone(),
        }
    }

    fn save(&mut self) {
        self.cfg.edges = Some(self.edges.clone());
        let mut failed = None;
        for row in &mut self.rows {
            self.cfg.users.insert(row.device.id.clone(), row.user.trim().to_string());
            if !row.password.is_empty() {
                if let Err(e) = secrets::set(&row.device.id, &row.password) {
                    failed = Some(format!("Couldn't save the password for {} in your keyring: {e}", row.device.name));
                }
                row.password.clear();
            }
        }
        self.status = match (self.cfg.save(), failed) {
            (Err(e), _) => format!("Couldn't save settings: {e}"),
            (_, Some(f)) => f,
            _ => "Saved.".into(),
        };
    }

    // ---------- sections ----------

    fn devices_ui(&mut self, ui: &mut egui::Ui, ctx: &egui::Context) {
        heading(ui, "Devices");
        if let Some(core) = &self.core {
            note(ui, &format!("This computer: {}", core.name));
        }
        if self.rows.is_empty() {
            note(ui, "Nothing paired yet. Pair with a computer running Async below.");
        }
        let mut remove = None;
        for (i, row) in self.rows.iter_mut().enumerate() {
            ui.horizontal(|ui| {
                let (dot, _) = ui.allocate_exact_size(Vec2::splat(12.0), Sense::hover());
                ui.painter().circle_filled(dot.center(), 5.0, PALETTE[i % PALETTE.len()]);
                ui.label(RichText::new(&row.label).strong());
            });
            ui.horizontal(|ui| {
                ui.add_space(18.0);
                if row.vnc_only {
                    ui.label(RichText::new("user: not needed").color(DIM));
                } else {
                    ui.add(egui::TextEdit::singleline(&mut row.user).hint_text("user to sign in as").desired_width(150.0));
                }
                ui.add(egui::TextEdit::singleline(&mut row.password).password(true).hint_text("new password").desired_width(150.0));
                if ui.button("Remove").clicked() {
                    remove = Some(row.device.clone());
                }
            });
        }
        if let (Some(d), Some(core)) = (remove, self.core.clone()) {
            let (tx, ctx2) = (self.tx.clone(), ctx.clone());
            std::thread::spawn(move || {
                let msg = match core.remove(&d.id) {
                    Ok(()) => {
                        secrets::delete(&d.id);
                        format!("Unpaired {}.", d.name)
                    }
                    Err(e) => format!("{e:#}"),
                };
                let _ = tx.send(Msg::Info(msg));
                ctx2.request_repaint();
            });
            self.load_devices(ctx);
        }
        note(ui, "Passwords are kept in your desktop keyring, on this computer only. Leave the box empty to keep the saved one.");

        ui.add_space(8.0);
        ui.label(RichText::new("Pair a device").color(DIM));
        ui.horizontal(|ui| {
            match &self.code {
                Some((code, until)) => {
                    let left = until.saturating_duration_since(Instant::now()).as_secs();
                    ui.label(RichText::new(code).size(24.0).strong().color(ACCENT));
                    ui.label(RichText::new(format!("type it on the other computer · {}:{:02}", left / 60, left % 60)).color(DIM));
                    if ui.button("Cancel").clicked() {
                        if let Some(core) = self.core.clone() {
                            std::thread::spawn(move || core.pair_cancel());
                        }
                        self.code = None;
                    }
                    ctx.request_repaint_after(Duration::from_secs(1));
                }
                None => {
                    if ui.button("Show a code").clicked() {
                        if let Some(core) = self.core.clone() {
                            let (tx, ctx2) = (self.tx.clone(), ctx.clone());
                            std::thread::spawn(move || {
                                let _ = tx.send(Msg::Code(core.pair_host().map_err(|e| format!("{e:#}"))));
                                ctx2.request_repaint();
                            });
                        }
                    }
                    ui.label(RichText::new("or").color(DIM));
                    ui.add(egui::TextEdit::singleline(&mut self.join_code).hint_text("ABCD-EFGH").desired_width(110.0));
                    if ui.button("Enter code").clicked() && !self.join_code.trim().is_empty() {
                        if let Some(core) = self.core.clone() {
                            let (tx, ctx2, code) = (self.tx.clone(), ctx.clone(), self.join_code.trim().to_uppercase());
                            self.status = "Pairing…".into();
                            std::thread::spawn(move || {
                                let _ = tx.send(Msg::Joined(core.pair_join(&code).map(|d| d.name).map_err(|e| format!("{e:#}"))));
                                ctx2.request_repaint();
                            });
                        }
                    }
                }
            }
        });
    }

    fn edges_ui(&mut self, ui: &mut egui::Ui) {
        heading(ui, "Screen edges");
        if self.monitors.is_empty() {
            note(ui, "Couldn't read your monitors (Async needs an X11 session for now).");
            return;
        }
        let width = ui.available_width().min(520.0);
        let (rect, response) = ui.allocate_exact_size(Vec2::new(width, 170.0), Sense::click());
        let painter = ui.painter_at(rect);
        let min_x = self.monitors.iter().map(|m| m.x).min().unwrap_or(0) as f32;
        let min_y = self.monitors.iter().map(|m| m.y).min().unwrap_or(0) as f32;
        let max_x = self.monitors.iter().map(|m| m.right()).max().unwrap_or(1) as f32;
        let max_y = self.monitors.iter().map(|m| m.bottom()).max().unwrap_or(1) as f32;
        let pad = 14.0;
        let k = ((rect.width() - pad * 2.0) / (max_x - min_x)).min((rect.height() - pad * 2.0) / (max_y - min_y));
        let ox = rect.left() + (rect.width() - (max_x - min_x) * k) / 2.0;
        let oy = rect.top() + (rect.height() - (max_y - min_y) * k) / 2.0;
        let hover = response.hover_pos();
        let mut clicked = None;

        for (n, m) in self.monitors.iter().enumerate() {
            let r = Rect::from_min_size(Pos2::new(ox + (m.x as f32 - min_x) * k, oy + (m.y as f32 - min_y) * k), Vec2::new(m.w as f32 * k, m.h as f32 * k)).shrink(3.0);
            painter.rect_filled(r, CornerRadius::same(3), MONITOR);
            painter.text(r.center() - Vec2::new(0.0, 8.0), egui::Align2::CENTER_CENTER, format!("{}", n + 1), egui::FontId::proportional(20.0), Color32::WHITE);
            painter.text(r.center() + Vec2::new(0.0, 12.0), egui::Align2::CENTER_CENTER, format!("{} · {}×{}", m.name, m.w, m.h), egui::FontId::proportional(11.0), DIM);
            for e in Edge::MAPPABLE {
                if !monitors::is_free(&self.monitors, m, e) {
                    continue;
                }
                let bar = edge_bar(r, e);
                let device = self.edges.get(&edge_key(&m.name, e)).filter(|d| !d.is_empty()).cloned();
                let hot = hover.is_some_and(|p| bar.expand(6.0).contains(p));
                let selected = self.picked.as_ref().is_some_and(|(name, pe)| *name == m.name && *pe == e);
                let mut col = device.as_deref().map(|d| self.color_of(d)).unwrap_or(UNSET);
                if hot || selected {
                    col = col.lerp_to_gamma(Color32::WHITE, 0.35);
                }
                painter.rect_filled(bar, CornerRadius::same(3), col);
                if device.is_some() && self.cfg.modes.get(&edge_key(&m.name, e)).is_some_and(|v| v == "mouse") {
                    // Extend: an arrow pointing out through the edge.
                    let c = bar.center();
                    let a = 4.0;
                    let tri = match e {
                        Edge::Top => vec![Pos2::new(c.x, c.y - a), Pos2::new(c.x - a * 1.6, c.y + a), Pos2::new(c.x + a * 1.6, c.y + a)],
                        Edge::Left => vec![Pos2::new(c.x - a, c.y), Pos2::new(c.x + a, c.y - a * 1.6), Pos2::new(c.x + a, c.y + a * 1.6)],
                        _ => vec![Pos2::new(c.x + a, c.y), Pos2::new(c.x - a, c.y - a * 1.6), Pos2::new(c.x - a, c.y + a * 1.6)],
                    };
                    painter.add(egui::Shape::convex_polygon(tri, Color32::WHITE, Stroke::NONE));
                }
                if selected {
                    painter.rect_stroke(bar.expand(2.0), CornerRadius::same(4), Stroke::new(1.5_f32, Color32::WHITE), egui::StrokeKind::Outside);
                }
                if hot && response.clicked() {
                    clicked = Some((m.name.clone(), e));
                }
            }
        }
        if clicked.is_some() {
            self.picked = clicked;
        }

        match self.picked.clone() {
            Some((name, e)) => {
                let key = edge_key(&name, e);
                let current = self.edges.get(&key).cloned().unwrap_or_default();
                let n = self.monitors.iter().position(|m| m.name == name).map(|i| i + 1).unwrap_or(0);
                ui.horizontal(|ui| {
                    ui.label(format!("Screen {n}, {} edge opens:", e.name()));
                    let text = if current.is_empty() { "Nothing".to_string() } else { self.name_of(&current) };
                    egui::ComboBox::from_id_salt("edge-device").selected_text(text).show_ui(ui, |ui| {
                        let mut choices = Vec::new();
                        for row in &self.rows {
                            if row.screens.len() > 1 {
                                // A computer with several screens: pick which one this edge shows.
                                for (n, s) in row.screens.iter().enumerate() {
                                    choices.push((format!("{}@{n}", row.device.id), format!("{} · {}", row.device.name, async_core::screen_label(s, n, row.screens.len()))));
                                }
                            } else {
                                choices.push((row.device.id.clone(), row.device.name.clone()));
                            }
                        }
                        for (value, label) in choices {
                            if ui.selectable_label(current == value, label).clicked() {
                                self.edges.insert(key.clone(), value);
                            }
                        }
                        if ui.selectable_label(current.is_empty(), "Nothing").clicked() {
                            self.edges.remove(&key);
                        }
                    });
                });
                if !current.is_empty() {
                    // What going through does: show the other screen here, or carry on onto the other computer.
                    let mut mouse = self.cfg.modes.get(&key).is_some_and(|v| v == "mouse");
                    ui.horizontal(|ui| {
                        ui.label("When the pointer goes through:");
                        ui.radio_value(&mut mouse, false, "Show its screen here (portal)");
                        ui.radio_value(&mut mouse, true, "Move the pointer over to it (extend)");
                    });
                    if mouse {
                        self.cfg.modes.insert(key.clone(), "mouse".into());
                    } else {
                        self.cfg.modes.remove(&key);
                    }
                }
            }
            None => note(ui, "Click the top or a side of a screen to choose which computer pushing through it opens, and whether its screen shows here (portal) or your pointer moves over to it (extend, marked with an arrow). The bottom is always the way back. Edges where two screens meet aren't shown: the mouse just crosses over there."),
        }
    }

    fn push_ui(&mut self, ui: &mut egui::Ui) {
        heading(ui, "Push to punch");
        ui.add(egui::Slider::new(&mut self.cfg.push_distance, 200..=2000).text("how hard"));
        ui.add(egui::Slider::new(&mut self.cfg.push_hold_ms, 0..=800).text("how long (ms)"));
        note(ui, "Push down out of the bottom of the screen to close the portal, whichever edge you came in through.");
    }
}

impl eframe::App for SettingsApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        while let Ok(msg) = self.rx.try_recv() {
            self.handle(msg, ctx);
        }
        egui::TopBottomPanel::bottom("buttons").show(ctx, |ui| {
            ui.add_space(6.0);
            ui.horizontal(|ui| {
                ui.label(RichText::new(&self.status).color(DIM));
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.add(egui::Button::new(RichText::new("Save").strong()).fill(Color32::from_rgb(120, 60, 200))).clicked() {
                        self.save();
                    }
                    if ui.button("Close").clicked() {
                        ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                    }
                });
            });
            ui.add_space(6.0);
        });
        egui::CentralPanel::default().show(ctx, |ui| {
            egui::ScrollArea::vertical().show(ui, |ui| {
                if let Some(e) = &self.error {
                    ui.colored_label(Color32::from_rgb(255, 120, 120), e);
                }
                self.devices_ui(ui, ctx);
                self.edges_ui(ui);
                self.push_ui(ui);
            });
        });
    }
}

fn heading(ui: &mut egui::Ui, text: &str) {
    ui.add_space(10.0);
    ui.label(RichText::new(text).size(17.0).color(Color32::from_rgb(200, 160, 255)));
    ui.add_space(2.0);
}

fn note(ui: &mut egui::Ui, text: &str) {
    ui.label(RichText::new(text).size(12.0).color(DIM));
}

fn edge_bar(r: Rect, e: Edge) -> Rect {
    let inset = r.width().min(r.height()) * 0.08;
    let t = 7.0;
    match e {
        Edge::Top => Rect::from_min_max(Pos2::new(r.left() + inset, r.top() - t / 2.0), Pos2::new(r.right() - inset, r.top() + t / 2.0)),
        Edge::Bottom => Rect::from_min_max(Pos2::new(r.left() + inset, r.bottom() - t / 2.0), Pos2::new(r.right() - inset, r.bottom() + t / 2.0)),
        Edge::Left => Rect::from_min_max(Pos2::new(r.left() - t / 2.0, r.top() + inset), Pos2::new(r.left() + t / 2.0, r.bottom() - inset)),
        Edge::Right => Rect::from_min_max(Pos2::new(r.right() - t / 2.0, r.top() + inset), Pos2::new(r.right() + t / 2.0, r.bottom() - inset)),
    }
}
