//! Async for Linux and macOS: push the mouse through a screen edge and punch a portal into another
//! computer.
//!
//! ```text
//! async-desktop              # run in the background with a tray icon (what login starts)
//! async-desktop settings     # the settings window
//! ```
//!
//! It drives the async-core service that install.sh sets up, over that service's local socket.

mod app;
mod capture;
mod config;
mod core;
mod edges;
mod files;
mod keymap;
mod monitors;
mod portal;
mod rdp;
mod settings;
mod share;
mod strips;
mod tray;
mod vnc;

use std::os::unix::net::{UnixListener, UnixStream};

use winit::event_loop::EventLoop;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.first().map(String::as_str) {
        Some("settings") => settings::run(),
        Some("--version") => {
            println!("async-desktop {}", env!("CARGO_PKG_VERSION"));
            Ok(())
        }
        None => run(),
        Some(other) => Err(anyhow::anyhow!("unknown command {other:?} (try: settings)")),
    };
    if let Err(e) = result {
        app::notify("Async", &format!("{e:#}"));
        std::process::exit(1);
    }
}

fn run() -> anyhow::Result<()> {
    // One copy per login: a second launch just opens Settings.
    let lock = config::folder().join("desktop.lock");
    std::fs::create_dir_all(config::folder())?;
    if UnixStream::connect(&lock).is_ok() {
        app::open_settings();
        return Ok(());
    }
    let _ = std::fs::remove_file(&lock);
    let _guard = UnixListener::bind(&lock)?;

    let core = core::Core::connect(|_| {})?;
    let mut builder = EventLoop::<app::AppEvent>::with_user_event();
    #[cfg(target_os = "macos")]
    {
        // A menu-bar app: no Dock icon, no app menu.
        use winit::platform::macos::{ActivationPolicy, EventLoopBuilderExtMacOS};
        builder.with_activation_policy(ActivationPolicy::Accessory);
    }
    let event_loop = builder.build()?;
    let proxy = event_loop.create_proxy();
    let mut app = app::App::new(core, proxy)?;
    event_loop.run_app(&mut app)?;
    Ok(())
}
