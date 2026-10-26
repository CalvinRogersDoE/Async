using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Async;

/// <summary>
/// Mouse mode, the receiving side: a paired computer's pointer and keyboard cross over to this PC.
/// The other computer's Async connects to the tunnel's <c>input</c> service, which async-core hands
/// to 127.0.0.1:24850; its moves, clicks, wheel turns and keys are injected here with SendInput.
/// The pointer appears on the edge of the whole desktop it came in by and goes back when it's
/// pushed out through that same edge (not an edge between two monitors). Held keys and buttons are
/// let go when it leaves or the link drops. The wire format is described in desktop/src/share.rs.
/// With ASYNC_INPUT_DRY_RUN=1 nothing is injected: each injection is written to stderr instead.
/// </summary>
sealed class InputReceive : IDisposable
{
    public const int Port = 24850;
    /// <summary>How far past the edge the pointer has to be pushed to go back (pixels).</summary>
    const int LeavePush = 12;

    readonly Control ui;
    readonly TcpListener listener = new(IPAddress.Loopback, Port);
    readonly bool dryRun = Environment.GetEnvironmentVariable("ASYNC_INPUT_DRY_RUN") == "1";
    int inside;
    long busyUntil;

    /// <summary>True while another computer is driving this PC (its edges then do nothing).</summary>
    public bool Active => Volatile.Read(ref inside) > 0 || Environment.TickCount64 < Volatile.Read(ref busyUntil);

    public InputReceive(Control ui)
    {
        this.ui = ui;
        try
        {
            listener.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "Async input listen" }.Start();
        }
        catch (Exception e) { Log.Write($"Receiving the pointer unavailable (port {Port} taken?): {e.Message}"); }
    }

    void AcceptLoop()
    {
        while (true)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { return; }
            new Thread(() => Serve(c)) { IsBackground = true, Name = "Async input in" }.Start();
        }
    }

    void Serve(TcpClient c)
    {
        var inject = new Injector(dryRun);
        var s = new Session();
        bool any = false;
        using (c)
        {
            try
            {
                c.NoDelay = true;
                var net = c.GetStream();
                var input = new BufferedStream(net, 1 << 16);
                while (true)
                {
                    int kind = input.ReadByte();
                    if (kind < 0) break; // includes async-core checking whether the service is up
                    any = true;
                    Volatile.Write(ref busyUntil, Environment.TickCount64 + 600);
                    switch (kind)
                    {
                        case 1:
                            var edge = (Edge)Math.Min(ReadU8(input), (byte)3);
                            float pos = Math.Clamp(ReadF32(input), 0, 1);
                            s.Screens = Screen.AllScreens.Select(x => x.Bounds).ToArray();
                            (s.X, s.Y) = s.EnterPoint(edge, pos);
                            if (s.Entry == null) Interlocked.Increment(ref inside);
                            (s.Entry, s.Pushed) = (edge, 0);
                            inject.MoveTo(s.X, s.Y);
                            Log.Write($"Mouse mode: pointer in from the other computer through the {edge} edge");
                            break;
                        case 2:
                            double dx = ReadI16(input), dy = ReadI16(input);
                            if (s.Entry == null) break; // moves still on their way after the pointer left
                            if (s.Apply(dx, dy) is { } leave)
                            {
                                inject.ReleaseAll();
                                var f = new byte[6];
                                f[0] = 6;
                                f[1] = (byte)leave.Edge;
                                BinaryPrimitives.WriteSingleLittleEndian(f.AsSpan(2), leave.Pos);
                                net.Write(f);
                                if (ClipboardText() is { } text) SendClipboard(net, text);
                                s.Entry = null;
                                Interlocked.Decrement(ref inside);
                                Log.Write($"Mouse mode: pointer back out through the {leave.Edge} edge at {leave.Pos:0.000}");
                                break;
                            }
                            inject.MoveTo(s.X, s.Y);
                            break;
                        case 3:
                            byte button = ReadU8(input);
                            bool down = ReadU8(input) != 0;
                            if (s.Entry != null || !down) inject.Button(button, down);
                            break;
                        case 4:
                            short wy = ReadI16(input), wx = ReadI16(input);
                            if (s.Entry != null) inject.Wheel(wy, wx);
                            break;
                        case 5:
                            ushort code = ReadU16(input);
                            bool keyDown = ReadU8(input) != 0;
                            if (s.Entry != null || !keyDown) inject.Key(code, keyDown);
                            break;
                        case 7:
                            var bytes = new byte[Math.Min(ReadU32(input), 16u << 20)];
                            input.ReadExactly(bytes);
                            SetClipboard(Encoding.UTF8.GetString(bytes));
                            break;
                        case 8:
                            inject.ReleaseAll();
                            break;
                        default:
                            throw new IOException($"unknown mouse-mode message {kind}");
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                if (any && e is not EndOfStreamException) Log.Write($"Mouse mode (receiving) ended: {e.Message}");
            }
            finally
            {
                inject.ReleaseAll();
                if (s.Entry != null) { Interlocked.Decrement(ref inside); Log.Write("Mouse mode: link to the other computer dropped"); }
            }
        }
    }

    // ---------- clipboard (the UI thread, which is STA) ----------

    string? ClipboardText()
    {
        if (dryRun) { Console.Error.WriteLine("dry run: would send this PC's clipboard back"); return null; }
        try { return (string?)ui.Invoke(() => Clipboard.ContainsText() ? Clipboard.GetText() : null); }
        catch { return null; }
    }

    void SetClipboard(string text)
    {
        if (dryRun) { Console.Error.WriteLine($"dry run: clipboard set ({text.Length} characters)"); return; }
        if (text.Length == 0) return;
        try { ui.BeginInvoke(() => { try { Clipboard.SetText(text); } catch { } }); } catch { }
    }

    static void SendClipboard(Stream net, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 16 << 20) return;
        var f = new byte[5 + bytes.Length];
        f[0] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(1), (uint)bytes.Length);
        bytes.CopyTo(f, 5);
        net.Write(f);
    }

    // ---------- where the pointer is ----------

    /// <summary>Where the crossed-over pointer is, and whether it's being pushed back out.</summary>
    sealed class Session
    {
        public Rectangle[] Screens = [];
        public double X, Y;
        public Edge? Entry;
        public int Pushed;

        /// <summary>The whole desktop's bounds (left, top, right, bottom).</summary>
        (double L, double T, double R, double B) Bounds()
        {
            if (Screens.Length == 0) return (0, 0, 1920, 1080);
            return (Screens.Min(m => m.Left), Screens.Min(m => m.Top), Screens.Max(m => m.Right), Screens.Max(m => m.Bottom));
        }

        public (double, double) EnterPoint(Edge edge, float pos)
        {
            var (l, t, r, b) = Bounds();
            var (x, y) = edge switch
            {
                Edge.Top => (l + (r - l) * pos, t + 1),
                Edge.Bottom => (l + (r - l) * pos, b - 2),
                Edge.Left => (l + 1, t + (b - t) * pos),
                _ => (r - 2, t + (b - t) * pos),
            };
            return Clamp(x, y);
        }

        /// <summary>Keeps a point on some monitor (the nearest one, if it fell into a gap between them).</summary>
        (double, double) Clamp(double x, double y)
        {
            var best = (x, y);
            double bestD = double.MaxValue;
            foreach (var m in Screens)
            {
                double cx = Math.Clamp(x, m.Left, m.Right - 1), cy = Math.Clamp(y, m.Top, m.Bottom - 1);
                double d = (cx - x) * (cx - x) + (cy - y) * (cy - y);
                if (d < bestD) { bestD = d; best = (cx, cy); }
            }
            return best;
        }

        /// <summary>Moves the pointer; returns where it left if it was pushed out through the edge it came in by.</summary>
        public (Edge Edge, float Pos)? Apply(double dx, double dy)
        {
            double nx = X + dx, ny = Y + dy;
            var (cx, cy) = Clamp(nx, ny);
            var (l, t, r, b) = Bounds();
            if (Entry is { } entry)
            {
                // Only the desktop's outer edge counts, not the edge between two monitors.
                int over = entry switch
                {
                    Edge.Left when cx <= l + 0.5 && nx < cx => (int)(cx - nx),
                    Edge.Right when cx >= r - 1.5 && nx > cx => (int)(nx - cx),
                    Edge.Top when cy <= t + 0.5 && ny < cy => (int)(cy - ny),
                    Edge.Bottom when cy >= b - 1.5 && ny > cy => (int)(ny - cy),
                    _ => 0,
                };
                Pushed = over > 0 ? Pushed + over : 0;
                if (Pushed >= LeavePush)
                {
                    double pos = entry.Horizontal() ? (cx - l) / (r - l) : (cy - t) / (b - t);
                    return (entry, (float)Math.Clamp(pos, 0, 1));
                }
            }
            (X, Y) = (cx, cy);
            return null;
        }
    }

    // ---------- injecting ----------

    /// <summary>Drives this PC's pointer and keyboard, remembering what's held so it can all be let go.</summary>
    sealed class Injector(bool dryRun)
    {
        readonly HashSet<ushort> keys = [];
        readonly HashSet<byte> buttons = [];
        bool warned;

        void Send(Native.INPUT input, string what)
        {
            if (dryRun) { Console.Error.WriteLine($"dry run: SendInput {what}"); return; }
            if (Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>()) == 0 && !warned)
            {
                // Blocked by UIPI (an elevated window has the focus) or the secure desktop (UAC, lock screen).
                warned = true;
                Log.Write($"Mouse mode: SendInput refused ({Marshal.GetLastWin32Error()}); an elevated window or the secure desktop may be in front");
            }
        }

        static Native.INPUT Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) =>
            new() { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };

        public void MoveTo(double x, double y)
        {
            int px = (int)Math.Round(x), py = (int)Math.Round(y);
            // Absolute moves are 0–65535 across the whole virtual desktop; the half-pixel lands each on the right pixel.
            var vs = SystemInformation.VirtualScreen;
            int nx = Math.Clamp((int)((px - vs.Left + 0.5) * 65536.0 / vs.Width), 0, 65535);
            int ny = Math.Clamp((int)((py - vs.Top + 0.5) * 65536.0 / vs.Height), 0, 65535);
            Send(Mouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, nx, ny), $"move abs {nx},{ny} (pixel {px},{py}){(buttons.Count > 0 ? " drag" : "")}");
            if (dryRun) return;
            // Some setups round absolute moves differently; put the pointer exactly where it belongs.
            if (Native.GetCursorPos(out var c) && (Math.Abs(c.X - px) > 1 || Math.Abs(c.Y - py) > 1)) Native.SetCursorPos(px, py);
        }

        public void Button(byte button, bool down)
        {
            var (flags, data, name) = button switch
            {
                1 => (down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP, 0u, "left"),
                2 => (down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP, 0u, "middle"),
                3 => (down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP, 0u, "right"),
                4 => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, Native.XBUTTON1, "back"),
                5 => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, Native.XBUTTON2, "forward"),
                _ => (0u, 0u, ""),
            };
            if (flags == 0) return;
            if (!down && !buttons.Contains(button)) return; // never let go of what this side didn't press
            Send(Mouse(flags, data: data), $"button {name} {(down ? "down" : "up")}");
            if (down) buttons.Add(button); else buttons.Remove(button);
        }

        public void Wheel(short dy, short dx)
        {
            // Both sides count in 1/120ths of a notch, positive away from the user and to the right.
            if (dy != 0) Send(Mouse(Native.MOUSEEVENTF_WHEEL, data: (uint)(int)dy), $"wheel {dy}");
            if (dx != 0) Send(Mouse(Native.MOUSEEVENTF_HWHEEL, data: (uint)(int)dx), $"hwheel {dx}");
        }

        public void Key(ushort code, bool down)
        {
            var (vk, scan, extended) = PcKey(code);
            if (vk == 0 && scan == 0) return;
            if (!down && !keys.Contains(code)) return;
            uint flags = (vk == 0 ? Native.KEYEVENTF_SCANCODE : 0) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : Native.KEYEVENTF_KEYUP);
            var input = new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };
            string what = vk != 0 ? $"vk 0x{vk:X2}" : $"scan 0x{scan:X2}";
            Send(input, $"key {what}{(extended ? " extended" : "")} {(down ? "down" : "up")} (evdev {code})");
            if (down) keys.Add(code); else keys.Remove(code);
        }

        public void ReleaseAll()
        {
            foreach (var code in keys.ToArray()) Key(code, false);
            foreach (var b in buttons.ToArray()) Button(b, false);
        }

        /// <summary>A Linux evdev key code as a PC key: scan code set 1 (or a virtual key for the odd ones out), and whether it's extended.</summary>
        static (ushort Vk, ushort Scan, bool Extended) PcKey(ushort code)
        {
            if (code == 119) return (0x13, 0x45, false); // Pause (E1 1D 45 on the wire, so sent as VK_PAUSE)
            if (code == 69) return (0x90, 0x45, true);   // Num Lock
            if (code is > 0 and <= 0x58) return (0, code, false); // evdev numbers these keys the same
            ushort scan = code switch
            {
                96 => 0x1C, 97 => 0x1D, 98 => 0x35, 99 => 0x37, 100 => 0x38,
                102 => 0x47, 103 => 0x48, 104 => 0x49, 105 => 0x4B, 106 => 0x4D,
                107 => 0x4F, 108 => 0x50, 109 => 0x51, 110 => 0x52, 111 => 0x53,
                125 => 0x5B, 126 => 0x5C, 127 => 0x5D,
                113 => 0x20, 114 => 0x2E, 115 => 0x30,
                164 => 0x22, 166 => 0x24, 165 => 0x10, 163 => 0x19,
                _ => 0,
            };
            if (scan != 0) return (0, scan, true);
            // F13–F16 (Mac keyboards have them), not extended.
            return code is >= 183 and <= 186 ? ((ushort)0, (ushort)(0x64 + code - 183), false) : default;
        }
    }

    // ---------- the wire ----------

    static byte ReadU8(Stream s) { int b = s.ReadByte(); return b < 0 ? throw new EndOfStreamException() : (byte)b; }
    static ushort ReadU16(Stream s) { Span<byte> b = stackalloc byte[2]; s.ReadExactly(b); return BinaryPrimitives.ReadUInt16LittleEndian(b); }
    static short ReadI16(Stream s) => (short)ReadU16(s);
    static uint ReadU32(Stream s) { Span<byte> b = stackalloc byte[4]; s.ReadExactly(b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
    static float ReadF32(Stream s) => BitConverter.Int32BitsToSingle((int)ReadU32(s));

    public void Dispose() => listener.Stop();
}
