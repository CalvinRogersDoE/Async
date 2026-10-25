//! This device's monitors, in desktop pixels with the main one at (0, 0), as Remote Desktop lays
//! them out. Shared with paired devices so they can open a session with the same layout and show
//! one screen of it.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Screen {
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
    pub primary: bool,
}

#[cfg(windows)]
pub fn list() -> Vec<Screen> {
    use windows_sys::Win32::Foundation::{LPARAM, RECT};
    use windows_sys::Win32::Graphics::Gdi::{EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITORINFO};
    use windows_sys::Win32::UI::HiDpi::{DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext};

    unsafe extern "system" fn each(monitor: HMONITOR, _: HDC, _: *mut RECT, data: LPARAM) -> i32 {
        let out = unsafe { &mut *(data as *mut Vec<Screen>) };
        let mut info: MONITORINFO = unsafe { std::mem::zeroed() };
        info.cbSize = std::mem::size_of::<MONITORINFO>() as u32;
        if unsafe { GetMonitorInfoW(monitor, &mut info) } != 0 {
            let r = info.rcMonitor;
            out.push(Screen { x: r.left, y: r.top, w: r.right - r.left, h: r.bottom - r.top, primary: info.dwFlags & 1 != 0 });
        }
        1
    }

    let mut screens = Vec::new();
    unsafe {
        // Real pixels, not the scaled-down ones Windows reports to programs that don't ask.
        let old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        EnumDisplayMonitors(std::ptr::null_mut(), std::ptr::null(), Some(each), &mut screens as *mut _ as LPARAM);
        SetThreadDpiAwarenessContext(old);
    }
    screens
}

/// macOS and Linux share one picture of the whole desktop (VNC), so there's nothing to pick.
#[cfg(not(windows))]
pub fn list() -> Vec<Screen> {
    Vec::new()
}
