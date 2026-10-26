//! The running app: turns edge pushes into portals (or, on edges set to extend, sends the pointer
//! over), plays the open/close animations, keeps the Remote Desktop session going while the portal
//! is hidden so the next punch is instant, and sends files dropped onto the portal or a drop strip.

use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime};

use winit::application::ApplicationHandler;
use winit::event::WindowEvent;
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoopProxy};
use winit::window::WindowId;

use crate::config::{Edge, Settings, secrets};
use crate::core::{self as async_core, Core, Device, RemoteScreen};
use crate::edges::{self, Cooldown, Push};
use crate::monitors::Monitor;
use crate::portal::Portal;
use crate::rdp;
use crate::share::Outgoing;
use crate::strips::{Dropped, Strips};
use crate::tray::{self, MenuCmd};

pub enum AppEvent {
    Push(Push),
    Menu(MenuCmd),
    /// The tunnel for session `epoch` is open (or failed).
    Tunnel { epoch: u64, result: Result<Opened, String> },
    Rdp { epoch: u64, out: rdp::Out },
    /// The left mouse button went down or up (drop strips come and go with it).
    Held(bool),
    /// Paired devices showing this computer in a portal right now.
    Viewers(Vec<String>),
    /// The pointer went over to another computer (mouse mode), or couldn't.
    Crossed(Result<Outgoing, String>),
    /// The pointer is back from the other computer.
    Back,
}

/// A portal's connection: Remote Desktop (Windows, some Linux) or VNC (Macs, Linux with x11vnc).
enum Session {
    Rdp(rdp::Session),
    Vnc(crate::vnc::Session),
}

impl Session {
    fn close(&mut self) {
        match self {
            Session::Rdp(s) => s.close(),
            Session::Vnc(s) => s.close(),
        }
    }
    fn release_all(&mut self) {
        match self {
            Session::Rdp(s) => s.release_all(),
            Session::Vnc(s) => s.release_all(),
        }
    }
    fn mouse_to(&mut self, x: f64, y: f64) {
        match self {
            Session::Rdp(s) => s.mouse_to(x, y),
            Session::Vnc(s) => s.mouse_to(x, y),
        }
    }
    fn window_input(&mut self, event: &WindowEvent, to_desktop: impl Fn(f64, f64) -> Option<(f64, f64)>) {
        match self {
            Session::Rdp(s) => s.window_input(event, to_desktop),
            Session::Vnc(s) => s.window_input(event, to_desktop),
        }
    }
}

/// A tunnel into a device's screen sharing, with what's needed to start the session.
pub struct Opened {
    host: String,
    port: u16,
    service: String,
    password: Option<String>,
    screens: Vec<RemoteScreen>,
    /// Screen sharing Async set up itself on the device (Linux) tells paired devices its password.
    vnc_password: Option<String>,
}

/// What pushing through an edge does.
enum Act {
    /// Open this device (switching from another one if a portal is open), on this screen of it.
    Open(String, Option<usize>),
    Close,
    /// Slide the open portal to this screen of the same device.
    Screen(usize),
}

type Then = (Monitor, Edge, i32, i32, String, Option<usize>);

#[derive(Clone, Copy, PartialEq)]
enum AnimKind {
    Open,
    Close,
}

struct Anim {
    kind: AnimKind,
    start: Instant,
    duration: Duration,
    center: (f32, f32),
    far: f32,
    /// What to open once a close finishes (switching computers).
    then: Option<Then>,
}

pub struct App {
    cfg: Arc<Mutex<Settings>>,
    cfg_stamp: Option<SystemTime>,
    core: Arc<Core>,
    rt: tokio::runtime::Runtime,
    proxy: EventLoopProxy<AppEvent>,
    cooldown: Cooldown,
    portal: Option<Portal>,
    anim: Option<Anim>,
    /// The device the portal shows, and the edge it came through.
    device: String,
    entry: Edge,
    session: Option<Session>,
    session_device: String,
    /// The session device's screens (in desk order) and the one the portal shows.
    screens: Vec<RemoteScreen>,
    screen: Option<usize>,
    epoch: u64,
    devices: Vec<Device>,
    devices_at: Option<Instant>,
    tray: Option<tray::Handle>,
    /// Mouse mode: the pointer is over on another computer while this is Some.
    outgoing: Option<Outgoing>,
    crossing: bool,
    strips: Strips,
    strips_close_at: Option<Instant>,
    viewers: Vec<String>,
    /// Files dropped (one event each) waiting a moment for the rest: (device, files, since).
    drops: Option<(String, Vec<PathBuf>, Instant)>,
}

impl App {
    pub fn new(core: Arc<Core>, proxy: EventLoopProxy<AppEvent>) -> anyhow::Result<Self> {
        let cfg = Arc::new(Mutex::new(Settings::load()));
        let cooldown = Cooldown::default();
        let p = proxy.clone();
        edges::spawn(cfg.clone(), cooldown.clone(), move |push| {
            let _ = p.send_event(AppEvent::Push(push));
        })?;
        // Mouse mode: another computer's pointer can cross over to this one.
        if let Err(e) = crate::share::spawn(cooldown.clone()) {
            eprintln!("mouse mode unavailable (port {} taken?): {e:#}", crate::share::PORT);
        }
        // Files dropped onto this computer from a paired one.
        if let Err(e) = crate::files::spawn() {
            eprintln!("receiving files unavailable (port {} taken?): {e:#}", crate::files::PORT);
        }
        // Watches the left button (drop strips appear while it's held) and who's viewing this computer.
        {
            let (p, core) = (proxy.clone(), core.clone());
            std::thread::Builder::new().name("watch".into()).spawn(move || {
                let (mut held, mut viewers, mut tick) = (false, Vec::<String>::new(), 0u32);
                loop {
                    std::thread::sleep(Duration::from_millis(60));
                    let now = crate::capture::left_button_held();
                    if now != held {
                        held = now;
                        if p.send_event(AppEvent::Held(now)).is_err() {
                            return;
                        }
                    }
                    tick += 1;
                    if tick % 33 == 0 {
                        let list = core.viewers().unwrap_or_default();
                        if list != viewers {
                            viewers = list.clone();
                            let _ = p.send_event(AppEvent::Viewers(list));
                        }
                    }
                }
            })?;
        }
        let rt = tokio::runtime::Builder::new_multi_thread().worker_threads(2).enable_all().build()?;
        Ok(Self {
            cfg,
            cfg_stamp: Settings::modified(),
            core,
            rt,
            proxy,
            cooldown,
            portal: None,
            anim: None,
            device: String::new(),
            entry: Edge::Top,
            session: None,
            session_device: String::new(),
            screens: Vec::new(),
            screen: None,
            epoch: 0,
            devices: Vec::new(),
            devices_at: None,
            tray: None,
            outgoing: None,
            crossing: false,
            strips: Strips::default(),
            strips_close_at: None,
            viewers: Vec::new(),
            drops: None,
        })
    }

    /// Picks up changes saved by the settings window, and refreshes the paired list now and then.
    fn refresh(&mut self) {
        let stamp = Settings::modified();
        if stamp != self.cfg_stamp {
            self.cfg_stamp = stamp;
            *self.cfg.lock().unwrap() = Settings::load();
        }
        if self.devices_at.is_none_or(|t| t.elapsed() > Duration::from_secs(10)) {
            if let Ok(list) = self.core.peers() {
                self.devices = list;
            }
            self.devices_at = Some(Instant::now());
        }
    }

    fn portal_here(&self, m: &Monitor) -> bool {
        self.portal.as_ref().is_some_and(|p| p.visible && p.monitor.as_ref().is_some_and(|pm| pm.name == m.name))
    }

    fn portal_open(&self) -> bool {
        self.portal.as_ref().is_some_and(|p| p.visible)
    }

    /// What pushing through an edge does: open a computer (or one of its screens), slide to the
    /// next screen, close the portal (always the bottom), or nothing.
    fn decide(&self, m: &Monitor, e: Edge) -> Option<Act> {
        let cfg = self.cfg.lock().unwrap();
        let first = self.devices.first().map(|d| d.id.as_str());
        let target = cfg.device_for(&m.name, e, first).filter(|(d, _)| self.devices.iter().any(|x| &x.id == d));
        if self.portal_here(m) {
            if e == Edge::Bottom {
                return Some(Act::Close);
            }
            if let Some((d, screen)) = &target {
                if *d != self.device || (screen.is_some() && *screen != self.screen) {
                    return Some(Act::Open(d.clone(), *screen));
                }
            }
            // The sides slide along the other computer's screens.
            if !e.horizontal() && self.screens.len() > 1 {
                let current = self.screen.unwrap_or(0);
                let next = match e {
                    Edge::Left => current.checked_sub(1),
                    _ => Some(current + 1).filter(|&n| n < self.screens.len()),
                };
                if let Some(n) = next {
                    return Some(Act::Screen(n));
                }
            }
            return None;
        }
        target.map(|(d, screen)| Act::Open(d, screen))
    }

    fn on_push(&mut self, push: Push) {
        if self.anim.is_some() {
            return;
        }
        self.refresh();
        // Building pushes would light the edge up; that needs a compositor-backed overlay, coming later.
        let Push::Fired { monitor, edge, x, y } = push else { return };
        let mouse = self.cfg.lock().unwrap().is_mouse_edge(&monitor.name, edge);
        if mouse && !self.portal_open() {
            if let Some(Act::Open(device, _)) = self.decide(&monitor, edge) {
                self.cross(monitor, edge, x, y, device);
            }
            return;
        }
        match self.decide(&monitor, edge) {
            Some(Act::Open(device, screen)) if self.portal_open() && device == self.device => {
                if let Some(n) = screen {
                    self.show_screen(n);
                }
            }
            Some(Act::Open(device, screen)) if self.portal_open() => self.start_close(edge, x, y, Some((monitor, edge, x, y, device, screen))),
            Some(Act::Open(device, screen)) => self.start_open(monitor, edge, x, y, device, screen),
            Some(Act::Screen(n)) => self.show_screen(n),
            Some(Act::Close) => self.start_close(edge, x, y, None),
            None => {}
        }
    }

    /// Mouse mode: sends the pointer and keyboard over to `device`; files being dragged go with it.
    fn cross(&mut self, m: Monitor, e: Edge, x: i32, y: i32, device: String) {
        if self.outgoing.is_some() || self.crossing {
            return;
        }
        self.crossing = true;
        let files = if edges::FILE_DRAG.load(Ordering::Relaxed) { std::mem::take(&mut self.strips.files) } else { Vec::new() };
        edges::FILE_DRAG.store(false, Ordering::Relaxed);
        let (core, proxy, name) = (self.core.clone(), self.proxy.clone(), self.name_of(&device));
        std::thread::spawn(move || {
            if !files.is_empty() {
                // End the drag here (it drops onto the strip, which ignores it) before taking the mouse.
                crate::capture::release_left();
                std::thread::sleep(Duration::from_millis(80));
            }
            let back = {
                let proxy = proxy.clone();
                move |_, _| {
                    let _ = proxy.send_event(AppEvent::Back);
                }
            };
            let result = crate::share::cross(&core, &device, &m, e, x, y, back).map_err(|err| format!("Couldn't move the pointer to {name}: {err:#}"));
            let ok = result.is_ok();
            let _ = proxy.send_event(AppEvent::Crossed(result));
            if ok && !files.is_empty() {
                crate::files::send(&core, &device, &name, &files);
            }
        });
    }

    /// Sends files to `device` (it asks where they go) on a thread of its own.
    fn send_files(&self, device: String, files: Vec<PathBuf>) {
        let (core, name) = (self.core.clone(), self.name_of(&device));
        std::thread::spawn(move || crate::files::send(&core, &device, &name, &files));
    }

    /// Drop strips for every edge a file drag can leave by right now.
    fn show_strips(&mut self, event_loop: &ActiveEventLoop) {
        self.refresh();
        let cfg = self.cfg.lock().unwrap().clone();
        let first = self.devices.first().map(|d| d.id.clone());
        let mut list = Vec::new();
        for m in crate::monitors::list_now() {
            for e in Edge::MAPPABLE {
                if cfg.is_mouse_edge(&m.name, e) {
                    if let Some((d, _)) = cfg.device_for(&m.name, e, first.as_deref()) {
                        list.push((m.clone(), e, Some(d)));
                    }
                }
            }
            if !self.viewers.is_empty() {
                list.push((m.clone(), Edge::Bottom, None));
            }
        }
        if !list.is_empty() {
            self.strips.show(event_loop, &list, cfg.color_in);
        }
    }

    fn start_open(&mut self, m: Monitor, e: Edge, x: i32, y: i32, device: String, screen: Option<usize>) {
        let Some(portal) = self.portal.as_mut() else { return };
        self.entry = e;
        self.device = device.clone();
        portal.show_on(&m);
        let center = m.edge_point(e, x, y);
        self.anim = Some(Anim { kind: AnimKind::Open, start: Instant::now(), duration: Duration::from_millis(self.cfg.lock().unwrap().open_ms), center, far: far_corner(center, &m), then: None });
        edges::cool_down(&self.cooldown, 2000);
        if screen.is_some() {
            self.screen = screen;
        }
        self.ensure_session(&m);
    }

    fn start_close(&mut self, e: Edge, x: i32, y: i32, then: Option<Then>) {
        let Some(m) = self.portal.as_ref().and_then(|p| p.monitor.clone()) else { return };
        let center = m.edge_point(e, x, y);
        self.anim = Some(Anim { kind: AnimKind::Close, start: Instant::now(), duration: Duration::from_millis(self.cfg.lock().unwrap().close_ms), center, far: far_corner(center, &m), then });
        edges::cool_down(&self.cooldown, 2000);
        if let Some(s) = self.session.as_mut() {
            s.release_all();
        }
    }

    /// Shows screen `n` of the other computer in the portal (its part of the session's picture).
    fn show_screen(&mut self, n: usize) {
        let n = n.min(self.screens.len().saturating_sub(1));
        self.screen = Some(n);
        let view = if self.screens.len() > 1 {
            let (x0, y0, _, _) = rdp::bounds(&self.screens);
            let s = self.screens[n];
            Some(((s.x - x0) as u32, (s.y - y0) as u32, s.w as u32, s.h as u32))
        } else {
            None
        };
        if let Some(p) = self.portal.as_mut() {
            p.set_view(view);
            if p.visible && self.anim.is_none() {
                p.window().request_redraw();
            }
        }
        edges::cool_down(&self.cooldown, 500);
    }

    /// Connects to the portal's device unless already connected to it.
    fn ensure_session(&mut self, m: &Monitor) {
        if self.session.is_some() && self.session_device == self.device {
            self.show_screen(self.screen.unwrap_or_else(|| self.screens.iter().position(|s| s.primary).unwrap_or(0)));
            return;
        }
        if let Some(mut old) = self.session.take() {
            old.close();
        }
        if let Some(p) = self.portal.as_mut() {
            p.clear_frame();
        }
        self.epoch += 1;
        let (epoch, device, core, proxy) = (self.epoch, self.device.clone(), self.core.clone(), self.proxy.clone());
        self.session_device = device.clone();
        let name = self.name_of(&device);
        let _ = m;
        // The tunnel and the keyring can both take a moment; keep the animation smooth meanwhile.
        std::thread::spawn(move || {
            let result = core
                .open_screen(&device)
                .map(|(host, port, service)| {
                    let info = core.describe(&device).ok();
                    Opened {
                        host,
                        port,
                        service,
                        password: secrets::get(&device),
                        screens: info.as_ref().map(|i| async_core::sorted(i.screens.clone())).unwrap_or_default(),
                        vnc_password: info.and_then(|i| i.vnc_password),
                    }
                })
                .map_err(|e| format!("Couldn't reach {name}: {e:#}"));
            let _ = proxy.send_event(AppEvent::Tunnel { epoch, result });
        });
    }

    fn queue_drop(&mut self, device: String, files: Vec<PathBuf>) {
        match &mut self.drops {
            Some((d, list, at)) if *d == device => {
                list.extend(files);
                *at = Instant::now();
            }
            _ => self.drops = Some((device, files, Instant::now())),
        }
    }

    fn name_of(&self, device: &str) -> String {
        self.devices.iter().find(|d| d.id == device).map(|d| d.name.clone()).unwrap_or_else(|| "the other computer".into())
    }

    fn on_tunnel(&mut self, epoch: u64, result: Result<Opened, String>) {
        if epoch != self.epoch {
            return;
        }
        let name = self.name_of(&self.device);
        let Opened { host, port, service, password, screens, vnc_password } = match result {
            Ok(v) => v,
            Err(e) => return self.fail(&e),
        };
        self.screens = screens;
        let wanted = self.screen.filter(|&n| n < self.screens.len());
        self.show_screen(wanted.unwrap_or_else(|| self.screens.iter().position(|s| s.primary).unwrap_or(0)));
        let user = self.cfg.lock().unwrap().user_for(&self.device);
        if service == "vnc" {
            // A Mac signs in with its own user name and password; x11vnc that Async set up needs nothing typed.
            let password = vnc_password.or(password).unwrap_or_default();
            let proxy = self.proxy.clone();
            match crate::vnc::Session::start(self.rt.handle(), &host, port, &user, &password, move |out| {
                let _ = proxy.send_event(AppEvent::Rdp { epoch, out });
            }) {
                Ok(s) => self.session = Some(Session::Vnc(s)),
                Err(e) => self.fail(&format!("Couldn't open screen sharing on {name}: {e:#}")),
            }
            return;
        }
        let Some(password) = password.filter(|_| !user.is_empty()) else {
            self.fail(&format!("No sign-in saved for {name} yet. Open Async's settings and enter its user and password."));
            open_settings();
            return;
        };
        let size = self.portal.as_ref().and_then(|p| p.monitor.as_ref()).map(|m| m.pixels()).map(|(w, h)| (w as u16, h as u16)).unwrap_or((1920, 1080));
        let proxy = self.proxy.clone();
        match Session::start(self.rt.handle(), &host, port, &user, &password, size, &self.screens, move |out| {
            let _ = proxy.send_event(AppEvent::Rdp { epoch, out });
        }) {
            Ok(s) => self.session = Some(Session::Rdp(s)),
            Err(e) => self.fail(&format!("Couldn't start Remote Desktop: {e:#}")),
        }
    }

    fn on_rdp(&mut self, epoch: u64, out: rdp::Out) {
        if epoch != self.epoch {
            return;
        }
        match out {
            rdp::Out::Frame { pixels, width, height } => {
                if let Some(p) = self.portal.as_mut() {
                    p.set_frame(pixels, width, height);
                    if p.visible && self.anim.is_none() {
                        p.window().request_redraw();
                    }
                }
            }
            rdp::Out::LoggedIn => {}
            rdp::Out::Ended(why) => {
                self.session = None;
                self.session_device.clear();
                if self.portal_open() {
                    self.fail(&why);
                }
            }
        }
    }

    /// Something went wrong: say so, and fold the portal back up.
    fn fail(&mut self, message: &str) {
        notify("Async", message);
        self.session = None;
        self.session_device.clear();
        if self.portal_open() && self.anim.as_ref().is_none_or(|a| a.kind != AnimKind::Close) {
            let (e, m) = (self.entry, self.portal.as_ref().and_then(|p| p.monitor.clone()));
            if let Some(m) = m {
                let (cx, cy) = (m.x + m.w / 2, m.y + m.h / 2);
                self.start_close(e, cx, cy, None);
            }
        }
    }

    fn on_menu(&mut self, cmd: MenuCmd, event_loop: &ActiveEventLoop) {
        match cmd {
            MenuCmd::PunchHere => {
                self.refresh();
                let Some(device) = self.devices.first().map(|d| d.id.clone()) else {
                    notify("Async", "Nothing is paired yet. Open Settings to pair a computer.");
                    return;
                };
                if self.portal_open() || self.anim.is_some() {
                    return;
                }
                let Some(m) = crate::monitors::list_now().into_iter().find(|m| m.primary).or_else(|| crate::monitors::list_now().into_iter().next()) else { return };
                let (x, y) = (m.x + m.w / 2, m.y);
                self.start_open(m, Edge::Top, x, y, device, None);
            }
            MenuCmd::Close => {
                if self.portal_open() && self.anim.is_none() {
                    if let Some(m) = self.portal.as_ref().and_then(|p| p.monitor.clone()) {
                        self.start_close(self.entry, m.x + m.w / 2, m.y + m.h / 2, None);
                    }
                }
            }
            MenuCmd::Disconnect => {
                if let Some(mut s) = self.session.take() {
                    s.close();
                }
                self.session_device.clear();
            }
            MenuCmd::NextScreen | MenuCmd::PrevScreen => {
                if self.portal_open() && self.screens.len() > 1 {
                    let current = self.screen.unwrap_or(0);
                    let count = self.screens.len();
                    let n = if matches!(cmd, MenuCmd::NextScreen) { (current + 1) % count } else { (current + count - 1) % count };
                    self.show_screen(n);
                    let label = async_core::screen_label(&self.screens[n], n, count);
                    notify("Async", &format!("Showing the {}", label.to_lowercase()));
                }
            }
            MenuCmd::Settings => open_settings(),
            MenuCmd::Quit => {
                if let Some(mut s) = self.session.take() {
                    s.close();
                }
                event_loop.exit();
            }
        }
    }

    /// One animation frame; returns whether it's still running.
    fn step(&mut self) -> bool {
        let Some(a) = self.anim.as_ref() else { return false };
        let t = (a.start.elapsed().as_secs_f32() / a.duration.as_secs_f32().max(0.001)).min(1.0);
        let (cx, cy) = a.center;
        let (kind, far) = (a.kind, a.far);
        let cfg = self.cfg.lock().unwrap().clone();
        let Some(p) = self.portal.as_mut() else { return false };
        let r = match kind {
            AnimKind::Open => far * in_out_cubic(t),
            AnimKind::Close => far * (1.0 - t * t * t),
        };
        let color = if kind == AnimKind::Open { cfg.color_in } else { cfg.color_out };
        p.set_circle(cx, cy, r);
        p.draw(Some((cx, cy, r, color)));
        if t < 1.0 {
            return true;
        }

        // Finished.
        let a = self.anim.take().unwrap();
        match a.kind {
            AnimKind::Open => {
                p.clear_shape();
                p.draw(None);
                if let Some(m) = p.monitor.clone() {
                    // Land on the other computer right where you punched through.
                    let (pw, ph) = m.pixels();
                    let lx = (cx as i32).clamp(4, pw as i32 - 5);
                    let ly = (cy as i32).clamp(4, ph as i32 - 5);
                    p.warp_pointer(m.x + (lx as f64 / m.scale) as i32, m.y + (ly as f64 / m.scale) as i32);
                    if let (Some(s), Some((dx, dy))) = (self.session.as_mut(), p.to_desktop(lx as f64, ly as f64)) {
                        s.mouse_to(dx, dy);
                    }
                }
                edges::cool_down(&self.cooldown, 400);
            }
            AnimKind::Close => {
                p.hide();
                edges::cool_down(&self.cooldown, 400);
                if let Some((m, e, x, y, device, screen)) = a.then {
                    self.start_open(m, e, x, y, device, screen);
                    return true;
                }
            }
        }
        false
    }
}

impl ApplicationHandler<AppEvent> for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.tray.is_none() {
            // Created here because macOS needs its menu-bar item made on the running main thread.
            let proxy = Mutex::new(self.proxy.clone());
            self.tray = tray::spawn(move |cmd| {
                let _ = proxy.lock().unwrap().send_event(AppEvent::Menu(cmd));
            });
        }
        if self.portal.is_none() {
            match Portal::new(event_loop) {
                Ok(p) => self.portal = Some(p),
                Err(e) => {
                    notify("Async", &format!("Couldn't create the portal window: {e:#}"));
                    event_loop.exit();
                }
            }
        }
    }

    fn user_event(&mut self, event_loop: &ActiveEventLoop, event: AppEvent) {
        match event {
            AppEvent::Push(p) => self.on_push(p),
            AppEvent::Menu(cmd) => self.on_menu(cmd, event_loop),
            AppEvent::Tunnel { epoch, result } => self.on_tunnel(epoch, result),
            AppEvent::Rdp { epoch, out } => self.on_rdp(epoch, out),
            AppEvent::Held(true) => {
                self.strips_close_at = None;
                if self.outgoing.is_none() && !self.strips.showing() {
                    self.show_strips(event_loop);
                }
            }
            // Kept a moment after the button goes up, so the drop itself still lands on a strip.
            AppEvent::Held(false) => self.strips_close_at = self.strips.showing().then(|| Instant::now() + Duration::from_millis(1500)),
            AppEvent::Viewers(list) => self.viewers = list,
            AppEvent::Crossed(result) => {
                self.crossing = false;
                match result {
                    Ok(out) => self.outgoing = Some(out),
                    Err(why) => {
                        notify("Async", &why);
                        edges::cool_down(&self.cooldown, 1500);
                    }
                }
            }
            AppEvent::Back => {
                self.outgoing = None;
                edges::cool_down(&self.cooldown, 600);
            }
        }
        if self.anim.is_some() {
            event_loop.set_control_flow(ControlFlow::WaitUntil(Instant::now()));
        }
    }

    fn window_event(&mut self, _event_loop: &ActiveEventLoop, _id: WindowId, event: WindowEvent) {
        if self.strips.owns(_id) {
            match event {
                WindowEvent::HoveredFile(path) => {
                    self.strips.hovered(path);
                    edges::FILE_DRAG.store(true, Ordering::Relaxed);
                }
                WindowEvent::DroppedFile(path) => {
                    if let Some(Dropped::ToViewer(files)) = self.strips.dropped(_id, path) {
                        if let Some(viewer) = self.viewers.first().cloned() {
                            self.queue_drop(viewer, files);
                        }
                    }
                }
                WindowEvent::RedrawRequested => self.strips.redraw(_id),
                _ => {}
            }
            return;
        }
        // Files dropped onto the portal go to the computer it shows.
        if let WindowEvent::DroppedFile(path) = &event {
            if self.portal_open() && !self.device.is_empty() {
                self.queue_drop(self.device.clone(), vec![path.clone()]);
            }
            return;
        }
        match &event {
            WindowEvent::RedrawRequested => {
                if self.anim.is_none() {
                    if let Some(p) = self.portal.as_mut() {
                        p.draw(None);
                    }
                }
            }
            // Closing the window (Alt+F4) just folds the portal away.
            WindowEvent::CloseRequested => self.on_menu(MenuCmd::Close, _event_loop),
            _ => {
                if self.anim.is_none() && self.portal_open() {
                    if let (Some(s), Some(p)) = (self.session.as_mut(), self.portal.as_ref()) {
                        s.window_input(&event, |x, y| p.to_desktop(x, y));
                    }
                }
            }
        }
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        let now = Instant::now();
        if self.strips_close_at.is_some_and(|t| now >= t) {
            self.strips_close_at = None;
            self.strips.hide();
            edges::FILE_DRAG.store(false, Ordering::Relaxed);
        }
        // A drop of several files arrives one event per file: send them together.
        let drops_due = self.drops.as_ref().map(|d| d.2 + Duration::from_millis(250));
        if drops_due.is_some_and(|t| now >= t) {
            if let Some((device, files, _)) = self.drops.take() {
                self.send_files(device, files);
            }
        }
        let later = [self.strips_close_at, self.drops.as_ref().map(|d| d.2 + Duration::from_millis(250))].into_iter().flatten().min();
        if self.step() {
            event_loop.set_control_flow(ControlFlow::WaitUntil(now + Duration::from_millis(14)));
        } else if let Some(t) = later {
            event_loop.set_control_flow(ControlFlow::WaitUntil(t));
        } else {
            event_loop.set_control_flow(ControlFlow::Wait);
        }
    }
}

/// How far the portal's circle must grow from `c` (monitor pixels) to cover the whole monitor.
fn far_corner(c: (f32, f32), m: &Monitor) -> f32 {
    let (w, h) = m.pixels();
    let (w, h) = (w as f32, h as f32);
    [(0.0, 0.0), (w, 0.0), (0.0, h), (w, h)].iter().map(|&(x, y)| ((x - c.0).powi(2) + (y - c.1).powi(2)).sqrt()).fold(0.0, f32::max) + 40.0
}

fn in_out_cubic(t: f32) -> f32 {
    if t < 0.5 { 4.0 * t * t * t } else { 1.0 - (-2.0 * t + 2.0).powi(3) / 2.0 }
}

pub fn open_settings() {
    if let Ok(exe) = std::env::current_exe() {
        detach(std::process::Command::new(exe).arg("settings"));
    }
}

/// A desktop notification (also printed, for the log).
pub fn notify(title: &str, body: &str) {
    eprintln!("{title}: {body}");
    #[cfg(target_os = "linux")]
    detach(std::process::Command::new("notify-send").args(["--app-name=Async", "--icon=network-workgroup", title, body]));
    #[cfg(target_os = "macos")]
    {
        let quote = |s: &str| format!("\"{}\"", s.replace('\\', "\\\\").replace('"', "\\\""));
        let script = format!("display notification {} with title {}", quote(body), quote(title));
        detach(std::process::Command::new("osascript").args(["-e", &script]));
    }
}

/// Starts a program and reaps it when it exits, so it doesn't linger as a zombie.
fn detach(cmd: &mut std::process::Command) {
    if let Ok(mut child) = cmd.spawn() {
        std::thread::spawn(move || child.wait());
    }
}
