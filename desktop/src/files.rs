//! Files from a paired computer: dropped onto a portal, or dragged across a mouse-mode edge.
//!
//! The other computer's Async connects to the `files` service, which async-core hands to this app
//! on 127.0.0.1:24851. Little-endian throughout:
//!
//! ```text
//! sender:   version u8 (1), from: u16 len + UTF-8 (the computer's name),
//!           count u32, then per entry: u16 len + UTF-8 relative path ('/'-separated; a folder
//!           ends with '/'), size u64
//! receiver: u8 1 + u16 len + UTF-8 folder name (where they're going), or u8 0 (not wanted)
//! sender:   each file's bytes, in order
//! receiver: u8 1 once everything is written
//! ```
//!
//! This computer asks where the files should go (a folder picker, starting at the folder picked
//! last time), so they land exactly where the person wants them.

use std::io::{BufReader, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Component, Path, PathBuf};

use anyhow::{Context, Result, bail};

pub const PORT: u16 = 24851;

pub fn spawn() -> Result<()> {
    let listener = TcpListener::bind(("127.0.0.1", PORT))?;
    std::thread::spawn(move || {
        for stream in listener.incoming().flatten() {
            std::thread::spawn(move || {
                if let Err(e) = receive(stream) {
                    eprintln!("receiving files failed: {e:#}");
                    crate::app::notify("Async", &format!("Receiving files failed: {e:#}"));
                }
            });
        }
    });
    Ok(())
}

struct Entry {
    path: String,
    size: u64,
}

fn receive(stream: TcpStream) -> Result<()> {
    let mut out = stream.try_clone()?;
    let mut input = BufReader::with_capacity(1 << 20, stream);
    let mut version = [0u8; 1];
    if input.read_exact(&mut version).is_err() {
        return Ok(()); // async-core checking whether the service is up
    }
    if version[0] != 1 {
        bail!("the other computer runs a newer Async; update this one");
    }
    let from = read_string(&mut input)?;
    let count = read_u32(&mut input)? as usize;
    let mut entries = Vec::with_capacity(count.min(100_000));
    for _ in 0..count {
        let path = read_string(&mut input)?;
        let size = read_u64(&mut input)?;
        entries.push(Entry { path, size });
    }

    let files = entries.iter().filter(|e| !e.path.ends_with('/')).count();
    let total: u64 = entries.iter().map(|e| e.size).sum();
    let what = summary(&entries, files);
    let Some(dest) = pick_folder(&format!("Where should {what} from {from} go?")) else {
        out.write_all(&[0])?;
        return Ok(());
    };
    let shown = dest.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| dest.display().to_string());
    let mut answer = vec![1];
    write_string(&mut answer, &shown);
    out.write_all(&answer)?;

    // Top-level names that already exist get " (2)", " (3)"… so nothing is overwritten.
    let mut renamed = std::collections::HashMap::<String, String>::new();
    let mut buf = vec![0u8; 1 << 20];
    for e in &entries {
        let rel = safe_relative(&e.path).with_context(|| format!("refusing the path {:?}", e.path))?;
        let mut parts = rel.components();
        let top = parts.next().map(|c| c.as_os_str().to_string_lossy().into_owned()).unwrap_or_default();
        let top = renamed.entry(top.clone()).or_insert_with(|| free_name(&dest, &top)).clone();
        let rest = parts.as_path();
        let target = if rest.as_os_str().is_empty() { dest.join(&top) } else { dest.join(&top).join(rest) };
        if e.path.ends_with('/') {
            std::fs::create_dir_all(&target)?;
            continue;
        }
        if let Some(parent) = target.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let mut file = std::fs::File::create(&target).with_context(|| format!("creating {}", target.display()))?;
        let mut left = e.size;
        while left > 0 {
            let n = left.min(buf.len() as u64) as usize;
            input.read_exact(&mut buf[..n])?;
            file.write_all(&buf[..n])?;
            left -= n as u64;
        }
    }
    out.write_all(&[1])?;
    crate::app::notify("Async", &format!("Got {what} ({}) from {from} in {shown}", size_text(total)));
    Ok(())
}

fn summary(entries: &[Entry], files: usize) -> String {
    let tops: std::collections::BTreeSet<&str> = entries.iter().map(|e| e.path.split('/').next().unwrap_or("")).collect();
    if tops.len() == 1 {
        let name = *tops.iter().next().unwrap();
        if entries.iter().any(|e| e.path.starts_with(&format!("{name}/"))) {
            return format!("the folder \u{201c}{name}\u{201d}");
        }
        return format!("\u{201c}{name}\u{201d}");
    }
    format!("{files} files")
}

fn size_text(bytes: u64) -> String {
    match bytes {
        b if b >= 1 << 30 => format!("{:.1} GB", b as f64 / (1u64 << 30) as f64),
        b if b >= 1 << 20 => format!("{:.1} MB", b as f64 / (1u64 << 20) as f64),
        b if b >= 1 << 10 => format!("{} KB", b >> 10),
        b => format!("{b} bytes"),
    }
}

/// A relative path that stays inside the destination: no root, no drive, no "..".
fn safe_relative(p: &str) -> Option<PathBuf> {
    let path = Path::new(p.trim_end_matches('/'));
    let mut out = PathBuf::new();
    for c in path.components() {
        match c {
            Component::Normal(part) => out.push(part),
            Component::CurDir => {}
            _ => return None,
        }
    }
    (!out.as_os_str().is_empty()).then_some(out)
}

fn free_name(dest: &Path, name: &str) -> String {
    if !dest.join(name).exists() {
        return name.to_string();
    }
    let (stem, ext) = match name.rfind('.') {
        Some(i) if i > 0 => (&name[..i], &name[i..]),
        _ => (name, ""),
    };
    (2..).map(|n| format!("{stem} ({n}){ext}")).find(|n| !dest.join(n).exists()).unwrap()
}

// ---------- choosing the folder ----------

fn last_folder_file() -> PathBuf {
    crate::config::folder().join("last-drop-folder")
}

fn downloads() -> PathBuf {
    let home = PathBuf::from(std::env::var_os("HOME").unwrap_or_default());
    let d = home.join("Downloads");
    if d.is_dir() { d } else { home }
}

/// Asks where the files go, starting where they went last time. None if the person cancels.
fn pick_folder(prompt: &str) -> Option<PathBuf> {
    let start = std::fs::read_to_string(last_folder_file()).ok().map(|s| PathBuf::from(s.trim())).filter(|p| p.is_dir()).unwrap_or_else(downloads);
    let picked = match platform_pick(prompt, &start) {
        Picked::Folder(p) => p,
        Picked::Cancelled => return None,
        // No folder picker on this computer: use the last folder (or Downloads) without asking.
        Picked::NoPicker => start,
    };
    let _ = std::fs::create_dir_all(crate::config::folder());
    let _ = std::fs::write(last_folder_file(), picked.to_string_lossy().as_bytes());
    Some(picked)
}

enum Picked {
    Folder(PathBuf),
    Cancelled,
    NoPicker,
}

fn run_picker(cmd: &str, args: &[&str]) -> Option<Picked> {
    let out = std::process::Command::new(cmd).args(args).output().ok()?; // not installed
    if !out.status.success() {
        return Some(Picked::Cancelled);
    }
    let path = String::from_utf8_lossy(&out.stdout).trim().to_string();
    let p = PathBuf::from(path);
    Some(if p.is_dir() { Picked::Folder(p) } else { Picked::Cancelled })
}

#[cfg(target_os = "macos")]
fn platform_pick(prompt: &str, start: &Path) -> Picked {
    let quote = |s: &str| s.replace('\\', "\\\\").replace('"', "\\\"");
    let script = format!(
        "activate\nPOSIX path of (choose folder with prompt \"{}\" default location (POSIX file \"{}\"))",
        quote(prompt),
        quote(&start.to_string_lossy())
    );
    run_picker("osascript", &["-e", &script]).unwrap_or(Picked::NoPicker)
}

#[cfg(not(target_os = "macos"))]
fn platform_pick(prompt: &str, start: &Path) -> Picked {
    let start = format!("{}/", start.display());
    run_picker("zenity", &["--file-selection", "--directory", &format!("--title={prompt}"), &format!("--filename={start}")])
        .or_else(|| run_picker("kdialog", &["--getexistingdirectory", &start, "--title", prompt]))
        .unwrap_or(Picked::NoPicker)
}

// ---------- sending ----------

/// Sends files and folders to a paired computer, which asks where they go. Blocks until done (or
/// declined), so call it from a thread of its own; tells the person how it went.
pub fn send(core: &crate::core::Core, device: &str, device_name: &str, paths: &[PathBuf]) {
    let result = (|| -> Result<String> {
        let entries = collect(paths);
        if entries.is_empty() {
            bail!("nothing to send");
        }
        let what = summary(&entries.iter().map(|(rel, _, size)| Entry { path: rel.clone(), size: *size }).collect::<Vec<_>>(), entries.iter().filter(|e| e.1.is_some()).count());
        let (host, port) = core.open_service(device, "files")?;
        send_to(&host, port, &entries)?.map(|folder| format!("Sent {what} to {folder} on {device_name}.")).ok_or_else(|| anyhow::anyhow!("{device_name} didn't take {what}"))
    })();
    match result {
        Ok(done) => crate::app::notify("Async", &done),
        Err(e) => crate::app::notify("Async", &format!("Couldn't send to {device_name}: {e:#}")),
    }
}

/// (relative path, the file to read or None for a folder, size)
type Outgoing = (String, Option<PathBuf>, u64);

fn collect(paths: &[PathBuf]) -> Vec<Outgoing> {
    fn walk(dir: &Path, rel: &str, out: &mut Vec<Outgoing>) {
        out.push((format!("{rel}/"), None, 0));
        let Ok(read) = std::fs::read_dir(dir) else { return };
        let mut items: Vec<_> = read.flatten().collect();
        items.sort_by_key(|e| e.file_name());
        for item in items {
            let name = item.file_name().to_string_lossy().into_owned();
            let path = item.path();
            // Symlinks are skipped: following them could send far more than was dropped.
            let Ok(kind) = item.file_type() else { continue };
            if kind.is_dir() {
                walk(&path, &format!("{rel}/{name}"), out);
            } else if kind.is_file() {
                let size = item.metadata().map(|m| m.len()).unwrap_or(0);
                out.push((format!("{rel}/{name}"), Some(path), size));
            }
        }
    }
    let mut out = Vec::new();
    for p in paths {
        let name = p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        if name.is_empty() {
            continue;
        }
        if p.is_dir() {
            walk(p, &name, &mut out);
        } else if p.is_file() {
            out.push((name, Some(p.clone()), p.metadata().map(|m| m.len()).unwrap_or(0)));
        }
    }
    out
}

/// Sends to a files service at host:port; the folder name they went into, or None if declined.
fn send_to(host: &str, port: u16, entries: &[Outgoing]) -> Result<Option<String>> {
    let stream = TcpStream::connect((host, port))?;
    stream.set_nodelay(true).ok();
    let mut input = BufReader::new(stream.try_clone()?);
    let mut out = std::io::BufWriter::with_capacity(1 << 20, stream);
    let mut head = vec![1u8];
    write_string(&mut head, &computer_name());
    head.extend_from_slice(&(entries.len() as u32).to_le_bytes());
    for (rel, _, size) in entries {
        write_string(&mut head, rel);
        head.extend_from_slice(&size.to_le_bytes());
    }
    out.write_all(&head)?;
    out.flush()?;
    // Waits as long as the person takes to pick a folder over there.
    let mut answer = [0u8; 1];
    input.read_exact(&mut answer)?;
    if answer[0] != 1 {
        return Ok(None);
    }
    let folder = read_string(&mut input)?;
    let mut buf = vec![0u8; 1 << 20];
    for (rel, path, size) in entries {
        let Some(path) = path else { continue };
        let mut file = std::fs::File::open(path).with_context(|| format!("opening {rel}"))?;
        let mut left = *size;
        while left > 0 {
            let want = left.min(buf.len() as u64) as usize;
            let n = file.read(&mut buf[..want])?;
            if n == 0 {
                bail!("{rel} got shorter while it was being sent");
            }
            out.write_all(&buf[..n])?;
            left -= n as u64;
        }
    }
    out.flush()?;
    input.read_exact(&mut answer)?;
    if answer[0] != 1 {
        bail!("the other computer stopped partway");
    }
    Ok(Some(folder))
}

/// This computer's name, as the other one shows it ("from …").
fn computer_name() -> String {
    let ask = |cmd: &str, args: &[&str]| std::process::Command::new(cmd).args(args).output().ok().and_then(|o| String::from_utf8(o.stdout).ok());
    cfg!(target_os = "macos")
        .then(|| ask("scutil", &["--get", "ComputerName"]))
        .flatten()
        .or_else(|| std::fs::read_to_string("/etc/hostname").ok())
        .or_else(|| ask("hostname", &[]))
        .map(|n| n.trim().to_string())
        .filter(|n| !n.is_empty())
        .unwrap_or_else(|| "another computer".into())
}

// ---------- wire helpers ----------

fn read_u16(r: &mut impl Read) -> Result<u16> {
    let mut b = [0u8; 2];
    r.read_exact(&mut b)?;
    Ok(u16::from_le_bytes(b))
}
fn read_u32(r: &mut impl Read) -> Result<u32> {
    let mut b = [0u8; 4];
    r.read_exact(&mut b)?;
    Ok(u32::from_le_bytes(b))
}
fn read_u64(r: &mut impl Read) -> Result<u64> {
    let mut b = [0u8; 8];
    r.read_exact(&mut b)?;
    Ok(u64::from_le_bytes(b))
}
fn read_string(r: &mut impl Read) -> Result<String> {
    let mut b = vec![0u8; read_u16(r)? as usize];
    r.read_exact(&mut b)?;
    Ok(String::from_utf8_lossy(&b).into_owned())
}
fn write_string(out: &mut Vec<u8>, s: &str) {
    let b = &s.as_bytes()[..s.len().min(u16::MAX as usize)];
    out.extend_from_slice(&(b.len() as u16).to_le_bytes());
    out.extend_from_slice(b);
}
