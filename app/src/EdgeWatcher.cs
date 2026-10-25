using System.Runtime.InteropServices;

namespace Async;

/// <summary>
/// Watches raw mouse movement and fires when the cursor is held against the top (or bottom)
/// edge of a monitor and the user keeps pushing. Raw input still reports movement after the
/// cursor stops at the edge, which is what makes "pushing through" detectable.
/// </summary>
sealed class EdgeWatcher : NativeWindow, IDisposable
{
    public event Action<Screen, Point>? PushedTop;
    public event Action<Screen, Point>? PushedBottom;
    public event Action? Hotkey;
    /// <summary>A push is building: screen, cursor, whether it's the top edge, and 0–1 progress toward firing.</summary>
    public event Action<Screen, Point, bool, float>? Pushing;

    readonly Settings cfg;
    readonly int headerSize = Marshal.SizeOf<Native.RAWINPUTHEADER>();
    readonly IntPtr buffer = Marshal.AllocHGlobal(256);
    int pushed, pushDir;
    long lastPush, pushStart, cooldownUntil;
    bool hotkeyRegistered;

    public EdgeWatcher(Settings cfg)
    {
        this.cfg = cfg;
        CreateHandle(new CreateParams { Caption = "Async.EdgeWatcher" });
        RegisterRawMouse();
        if (cfg.TryParseHotkey(out uint mods, out uint vk))
        {
            hotkeyRegistered = Native.RegisterHotKey(Handle, 1, mods, vk);
            if (!hotkeyRegistered) Log.Write($"Hotkey {cfg.Hotkey} is taken by another app.");
        }
    }

    public void RegisterRawMouse()
    {
        var dev = new[] { new Native.RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = Native.RIDEV_INPUTSINK, Target = Handle } };
        if (!Native.RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            Log.Write($"RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>Ignore pushes for a moment, e.g. while an animation plays.</summary>
    public void Cooldown(int ms) => cooldownUntil = Environment.TickCount64 + ms;

    public long LastRawInput { get; private set; }

    static readonly bool Debug = Environment.GetEnvironmentVariable("ASYNC_DEBUG") == "1";

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_INPUT) OnRawInput(m.LParam);
        else if (m.Msg == Native.WM_HOTKEY) Hotkey?.Invoke();
        base.WndProc(ref m);
    }

    void OnRawInput(IntPtr raw)
    {
        uint size = 256;
        if (Native.GetRawInputData(raw, Native.RID_INPUT, buffer, ref size, (uint)headerSize) == uint.MaxValue) return;
        if (Marshal.ReadInt32(buffer, 0) != 0) return;                   // not RIM_TYPEMOUSE
        if ((Marshal.ReadInt16(buffer, headerSize) & 1) != 0) return;    // absolute (tablet / remote) devices
        int dy = Marshal.ReadInt32(buffer, headerSize + 16);
        LastRawInput = Environment.TickCount64;
        if (dy != 0) Track(dy);
    }

    void Track(int dy)
    {
        long now = Environment.TickCount64;
        Native.GetCursorPos(out var c);
        var p = new Point(c.X, c.Y);
        if (Debug) Log.Write($"raw dy={dy} cursor={p} buttons={ButtonsDown()} captured={CursorCaptured()} cooldown={now < cooldownUntil}");
        if (now < cooldownUntil || ButtonsDown() || CursorCaptured()) { pushed = 0; return; }

        var screen = Screen.FromPoint(p);
        var b = screen.Bounds;
        bool atTop = p.Y <= b.Top && !ScreenAt(p.X, b.Top - 1);
        bool atBottom = p.Y >= b.Bottom - 1 && !ScreenAt(p.X, b.Bottom);
        int dir = dy < 0 && atTop ? -1 : dy > 0 && atBottom ? 1 : 0;

        if (dir == 0 || dir != pushDir || now - lastPush > cfg.PushGapMs) { pushed = 0; pushStart = now; }
        pushDir = dir;
        lastPush = now;
        if (dir == 0) return;

        pushed += Math.Abs(dy);
        // How close the push is to firing: whichever of distance and duration is further behind.
        float progress = Math.Min((float)pushed / cfg.PushDistance, cfg.PushHoldMs <= 0 ? 1 : (float)(now - pushStart) / cfg.PushHoldMs);
        Pushing?.Invoke(screen, p, dir < 0, Math.Clamp(progress, 0, 1));
        if (pushed < cfg.PushDistance || now - pushStart < cfg.PushHoldMs) return;

        pushed = 0;
        Cooldown(700);
        Log.Write($"Push {(dir < 0 ? "top" : "bottom")} on {screen.DeviceName} at {p}");
        (dir < 0 ? PushedTop : PushedBottom)?.Invoke(screen, p);
    }

    static bool ScreenAt(int x, int y) => Screen.AllScreens.Any(s => s.Bounds.Contains(x, y));

    // Dragging a window to the top edge is Windows snap, not a punch.
    static bool ButtonsDown() =>
        (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 ||
        (Native.GetAsyncKeyState(0x02) & 0x8000) != 0 ||
        (Native.GetAsyncKeyState(0x04) & 0x8000) != 0;

    // Games hide or clip the cursor; don't punch out of a game by flicking the camera up.
    static bool CursorCaptured()
    {
        var ci = new Native.CURSORINFO { Size = Marshal.SizeOf<Native.CURSORINFO>() };
        if (Native.GetCursorInfo(ref ci) && (ci.Flags & 1) == 0) return true;
        if (!Native.GetClipCursor(out var clip)) return false;
        var v = SystemInformation.VirtualScreen;
        return clip.Right - clip.Left < v.Width || clip.Bottom - clip.Top < v.Height;
    }

    public void Dispose()
    {
        if (hotkeyRegistered) Native.UnregisterHotKey(Handle, 1);
        DestroyHandle();
        Marshal.FreeHGlobal(buffer);
    }
}
