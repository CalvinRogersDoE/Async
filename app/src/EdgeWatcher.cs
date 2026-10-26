using System.Runtime.InteropServices;

namespace Async;

enum Edge { Top, Bottom, Left, Right }

static class Edges
{
    public static readonly Edge[] All = [Edge.Top, Edge.Bottom, Edge.Left, Edge.Right];
    /// <summary>The edges that can lead to a computer; the bottom is always the way back.</summary>
    public static readonly Edge[] Mappable = [Edge.Top, Edge.Left, Edge.Right];
    public static Edge Opposite(this Edge e) => e switch { Edge.Top => Edge.Bottom, Edge.Bottom => Edge.Top, Edge.Left => Edge.Right, _ => Edge.Left };
    public static bool Horizontal(this Edge e) => e is Edge.Top or Edge.Bottom;

    /// <summary>Where on the edge of <paramref name="b"/> a screen point lands, in monitor-local pixels.</summary>
    public static PointF Point(this Edge e, Rectangle b, Point p)
    {
        float x = Math.Clamp(p.X - b.Left, 0, b.Width), y = Math.Clamp(p.Y - b.Top, 0, b.Height);
        return e switch { Edge.Top => new(x, 0), Edge.Bottom => new(x, b.Height), Edge.Left => new(0, y), _ => new(b.Width, y) };
    }
}

/// <summary>
/// Watches raw mouse movement and fires when the cursor is held against an outer edge of a
/// monitor and the user keeps pushing. Raw input still reports movement after the cursor stops
/// at the edge, which is what makes "pushing through" detectable. Edges where the mouse simply
/// crosses onto another monitor never count.
/// </summary>
sealed class EdgeWatcher : NativeWindow, IDisposable
{
    public event Action<Screen, Edge, Point>? Pushed;
    public event Action? Hotkey;
    /// <summary>A push is building: screen, edge, cursor, and 0–1 progress toward firing.</summary>
    public event Action<Screen, Edge, Point, float>? Pushing;
    /// <summary>True while edges should do nothing (the pointer is over on another computer).</summary>
    public Func<bool>? Paused;

    readonly Settings cfg;
    readonly int headerSize = Marshal.SizeOf<Native.RAWINPUTHEADER>();
    readonly IntPtr buffer = Marshal.AllocHGlobal(256);
    int pushed;
    Edge? pushEdge;
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
        int dx = Marshal.ReadInt32(buffer, headerSize + 12);
        int dy = Marshal.ReadInt32(buffer, headerSize + 16);
        LastRawInput = Environment.TickCount64;
        if (dx != 0 || dy != 0) Track(dx, dy);
    }

    void Track(int dx, int dy)
    {
        long now = Environment.TickCount64;
        Native.GetCursorPos(out var c);
        var p = new Point(c.X, c.Y);
        if (Debug) Log.Write($"raw dx={dx} dy={dy} cursor={p} buttons={ButtonsDown()} captured={CursorCaptured()} cooldown={now < cooldownUntil}");
        if (now < cooldownUntil || Paused?.Invoke() == true || ButtonsDown() || CursorCaptured()) { pushed = 0; return; }

        var screen = Screen.FromPoint(p);
        var b = screen.Bounds;
        // In a corner, the push goes to whichever edge it's pressing into harder.
        Edge? edge = null;
        int amount = 0;
        void Consider(Edge e, bool against, int d)
        {
            if (against && Math.Abs(d) > amount) { edge = e; amount = Math.Abs(d); }
        }
        Consider(Edge.Top, dy < 0 && p.Y <= b.Top && !ScreenAt(p.X, b.Top - 1), dy);
        Consider(Edge.Bottom, dy > 0 && p.Y >= b.Bottom - 1 && !ScreenAt(p.X, b.Bottom), dy);
        Consider(Edge.Left, dx < 0 && p.X <= b.Left && !ScreenAt(b.Left - 1, p.Y), dx);
        Consider(Edge.Right, dx > 0 && p.X >= b.Right - 1 && !ScreenAt(b.Right, p.Y), dx);

        // Sliding along the edge while pushing into it (no movement across it) neither adds nor resets.
        if (edge == null && pushEdge != null && (pushEdge.Value.Horizontal() ? dy : dx) == 0) return;

        if (edge == null || edge != pushEdge || now - lastPush > cfg.PushGapMs) { pushed = 0; pushStart = now; }
        pushEdge = edge;
        lastPush = now;
        if (edge is not { } e) return;

        pushed += amount;
        // Mouse-mode edges need only a light push, with no minimum duration.
        bool mouse = cfg.IsMouseEdge(screen.DeviceName, e);
        int distance = mouse ? cfg.MousePushDistance : cfg.PushDistance, hold = mouse ? 0 : cfg.PushHoldMs;
        // How close the push is to firing: whichever of distance and duration is further behind.
        float progress = Math.Min((float)pushed / distance, hold <= 0 ? 1 : (float)(now - pushStart) / hold);
        Pushing?.Invoke(screen, e, p, Math.Clamp(progress, 0, 1));
        if (pushed < distance || now - pushStart < hold) return;

        pushed = 0;
        Cooldown(700);
        Log.Write($"Push {e} on {screen.DeviceName} at {p}");
        Pushed?.Invoke(screen, e, p);
    }

    /// <summary>Whether part of this monitor edge is free (not joined to another monitor), so it can be pushed through.</summary>
    public static bool IsFree(Rectangle b, Edge e)
    {
        for (int i = 0; i < 24; i++)
        {
            int x = b.Left + (int)((i + 0.5) * b.Width / 24), y = b.Top + (int)((i + 0.5) * b.Height / 24);
            bool taken = e switch
            {
                Edge.Top => ScreenAt(x, b.Top - 1),
                Edge.Bottom => ScreenAt(x, b.Bottom),
                Edge.Left => ScreenAt(b.Left - 1, y),
                _ => ScreenAt(b.Right, y),
            };
            if (!taken) return true;
        }
        return false;
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
