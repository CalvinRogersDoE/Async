using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Async;

/// <summary>
/// Mouse mode, the sending side: the pointer and keyboard cross over to a paired computer instead
/// of a portal opening. While they're over there, low-level hooks keep every mouse and key event
/// from this PC and send it through the tunnel's <c>input</c> service; Windows' own cursor waits,
/// hidden, in the middle of the screen it left from (so moves in every direction can be measured).
/// The other computer says when its pointer is pushed back out, and it reappears here at the same
/// spot on the edge. The wire format is described in desktop/src/share.rs.
/// </summary>
sealed class MouseShare : IDisposable
{
    readonly Settings cfg;
    readonly CoreClient core;
    readonly Control ui;
    TcpClient? client;
    NetworkStream? net;
    IntPtr mouseHook, keyHook;
    readonly Native.HookProc mouseProc, keyProc;
    Form? blank;
    Screen? screen;
    Edge edge;
    Point park;
    string device = "";
    readonly HashSet<int> keysDown = [];
    // Outgoing frames, sent in order by one thread so the hooks never wait on the network.
    readonly List<byte[]> outbox = [];
    readonly AutoResetEvent outboxReady = new(false);

    public bool Active { get; private set; }
    bool live;
    /// <summary>Where along the edge the pointer last came back (tests).</summary>
    internal float? LastReturn { get; private set; }
    internal Point Park => park;
    /// <summary>The pointer came back (or the link dropped): screen, edge, point on that edge.</summary>
    public event Action<Point>? Returned;

    public MouseShare(Settings cfg, CoreClient core, Control ui)
    {
        this.cfg = cfg;
        this.core = core;
        this.ui = ui;
        mouseProc = OnMouse;
        keyProc = OnKey;
    }

    /// <summary>Sends the pointer over to <paramref name="toDevice"/>, entering where it left this screen.</summary>
    public async Task Start(string toDevice, Screen s, Edge e, Point at)
    {
        if (Active) return;
        var (host, port, _) = await core.OpenService(toDevice, "input");
        await StartAt(host, port, toDevice, s, e, at, live: true);
    }

    /// <summary>
    /// Connects to an input service and hands the pointer over. <paramref name="live"/> false leaves
    /// this PC's cursor and hooks alone (tests feed events in through <see cref="OnMouse"/>).
    /// </summary>
    internal async Task StartAt(string host, int port, string toDevice, Screen s, Edge e, Point at, bool live)
    {
        var c = new TcpClient { NoDelay = true };
        await c.ConnectAsync(host, port);
        (client, net, device, screen, edge) = (c, c.GetStream(), toDevice, s, e);
        var b = s.Bounds;
        float pos = e.Horizontal() ? (at.X - b.Left) / (float)b.Width : (at.Y - b.Top) / (float)b.Height;

        var enter = new byte[6];
        enter[0] = 1;
        enter[1] = (byte)e.Opposite(); // the edge it comes in by over there
        BinaryPrimitives.WriteSingleLittleEndian(enter.AsSpan(2), Math.Clamp(pos, 0, 1));
        Send(enter);
        try { if (Clipboard.ContainsText()) SendClipboard(Clipboard.GetText()); } catch { }

        Active = true;
        new Thread(SendLoop) { IsBackground = true, Name = "Async mouse out" }.Start();
        new Thread(ReadLoop) { IsBackground = true, Name = "Async mouse in" }.Start();

        park = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        this.live = live;
        if (!live) return;
        ShowBlankCursor();
        Native.SetCursorPos(park.X, park.Y);
        IntPtr module = Native.GetModuleHandle(null);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
        keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, module, 0);
        Log.Write($"Mouse mode: pointer over to {toDevice} through the {e} edge of {s.DeviceName}");
    }

    /// <summary>Brings the pointer back to this PC at the given spot on the edge it left by (0–1 along it).</summary>
    void Stop(float? pos, bool tellOther)
    {
        if (!Active) return;
        Active = false;
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
        mouseHook = keyHook = IntPtr.Zero;
        if (!live) { Active = false; try { client?.Close(); } catch { } Returned?.Invoke(Point.Empty); LastReturn = pos; return; }
        if (tellOther) { Send([8]); Thread.Sleep(30); }
        try { client?.Close(); } catch { }
        client = null;
        net = null;
        outboxReady.Set();
        keysDown.Clear();
        blank?.Hide();

        var b = screen!.Bounds;
        float p = Math.Clamp(pos ?? 0.5f, 0, 1);
        var back = edge switch
        {
            Edge.Left => new Point(b.Left + 2, b.Top + (int)(p * (b.Height - 1))),
            Edge.Right => new Point(b.Right - 3, b.Top + (int)(p * (b.Height - 1))),
            Edge.Top => new Point(b.Left + (int)(p * (b.Width - 1)), b.Top + 2),
            _ => new Point(b.Left + (int)(p * (b.Width - 1)), b.Bottom - 3),
        };
        Native.SetCursorPos(back.X, back.Y);
        Log.Write($"Mouse mode: pointer back from {device}");
        Returned?.Invoke(back);
    }

    /// <summary>Takes the pointer back right away (the hotkey, or Async closing).</summary>
    public void Recall() => Stop(null, tellOther: true);

    // ---------- hooks ----------

    internal IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !Active) return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
        if ((m.flags & 1) != 0) return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam); // injected by software
        int msg = (int)wParam;
        short high = (short)(m.mouseData >> 16);
        switch (msg)
        {
            case 0x200: // move: how far it would have gone from where the cursor waits
                int dx = m.pt.X - park.X, dy = m.pt.Y - park.Y;
                if (dx != 0 || dy != 0) SendMove(dx, dy);
                break;
            case 0x201: SendButton(1, true); break;
            case 0x202: SendButton(1, false); break;
            case 0x204: SendButton(3, true); break;
            case 0x205: SendButton(3, false); break;
            case 0x207: SendButton(2, true); break;
            case 0x208: SendButton(2, false); break;
            case 0x20B: SendButton(high == 1 ? (byte)4 : (byte)5, true); break;
            case 0x20C: SendButton(high == 1 ? (byte)4 : (byte)5, false); break;
            case 0x20A: SendWheel(high, 0); break;
            case 0x20E: SendWheel(0, high); break;
        }
        return 1; // keep it from this PC
    }

    internal IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !Active) return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        if ((k.flags & 0x10) != 0) return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam); // injected
        bool down = (k.flags & 0x80) == 0;
        if (down) keysDown.Add(k.vkCode); else keysDown.Remove(k.vkCode);

        // Async's hotkey brings the pointer straight back.
        if (down && cfg.TryParseHotkey(out uint mods, out uint vk) && k.vkCode == vk && ModsHeld(mods))
        {
            ui.BeginInvoke(Recall);
            return 1;
        }
        int evdev = Evdev(k.vkCode, k.scanCode, (k.flags & 1) != 0);
        if (evdev != 0)
        {
            var f = new byte[4];
            f[0] = 5;
            BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(1), (ushort)evdev);
            f[3] = (byte)(down ? 1 : 0);
            Send(f);
        }
        return 1;
    }

    bool ModsHeld(uint mods)
    {
        bool Any(params int[] vks) => vks.Any(keysDown.Contains);
        return ((mods & 0x2) == 0 || Any(0xA2, 0xA3, 0x11))
            && ((mods & 0x1) == 0 || Any(0xA4, 0xA5, 0x12))
            && ((mods & 0x4) == 0 || Any(0xA0, 0xA1, 0x10))
            && ((mods & 0x8) == 0 || Any(0x5B, 0x5C));
    }

    /// <summary>A PC key (scan code set 1) as a Linux evdev key code, the code mouse mode sends.</summary>
    static int Evdev(int vk, int scan, bool extended)
    {
        if (vk == 0x13) return 119; // Pause
        if (vk == 0x90) return 69;  // Num Lock (reported as an extended scan code 0x45)
        if (!extended) return scan is > 0 and <= 0x58 ? scan : 0; // evdev numbers these keys the same
        return scan switch
        {
            0x1C => 96, 0x1D => 97, 0x35 => 98, 0x37 => 99, 0x38 => 100,
            0x47 => 102, 0x48 => 103, 0x49 => 104, 0x4B => 105, 0x4D => 106,
            0x4F => 107, 0x50 => 108, 0x51 => 109, 0x52 => 110, 0x53 => 111,
            0x5B => 125, 0x5C => 126, 0x5D => 127,
            0x20 => 113, 0x2E => 114, 0x30 => 115,
            0x22 => 164, 0x24 => 166, 0x10 => 165, 0x19 => 163,
            _ => 0,
        };
    }

    // ---------- the wire ----------

    void SendMove(int dx, int dy)
    {
        var f = new byte[5];
        f[0] = 2;
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(1), (short)Math.Clamp(dx, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(3), (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
        Send(f);
    }

    void SendButton(byte button, bool down) => Send([3, button, (byte)(down ? 1 : 0)]);

    void SendWheel(short dy, short dx)
    {
        var f = new byte[5];
        f[0] = 4;
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(1), dy);
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(3), dx);
        Send(f);
    }

    void SendClipboard(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 16 << 20) return;
        var f = new byte[5 + bytes.Length];
        f[0] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(1), (uint)bytes.Length);
        bytes.CopyTo(f, 5);
        Send(f);
    }

    void Send(byte[] frame)
    {
        lock (outbox) outbox.Add(frame);
        outboxReady.Set();
    }

    /// <summary>Writes queued frames, merging moves that piled up into one so the pointer never lags behind.</summary>
    void SendLoop()
    {
        var stream = net;
        var batch = new MemoryStream();
        while (stream != null && (Active || outbox.Count > 0))
        {
            outboxReady.WaitOne(500);
            byte[][] frames;
            lock (outbox) { frames = [.. outbox]; outbox.Clear(); }
            if (frames.Length == 0) continue;
            batch.SetLength(0);
            int mx = 0, my = 0;
            bool moving = false;
            void FlushMove()
            {
                if (!moving) return;
                var f = new byte[5];
                f[0] = 2;
                BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(1), (short)Math.Clamp(mx, short.MinValue, short.MaxValue));
                BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(3), (short)Math.Clamp(my, short.MinValue, short.MaxValue));
                batch.Write(f);
                (mx, my, moving) = (0, 0, false);
            }
            foreach (var f in frames)
            {
                if (f[0] == 2)
                {
                    mx += BinaryPrimitives.ReadInt16LittleEndian(f.AsSpan(1));
                    my += BinaryPrimitives.ReadInt16LittleEndian(f.AsSpan(3));
                    moving = true;
                    continue;
                }
                FlushMove();
                batch.Write(f);
            }
            FlushMove();
            try { stream.Write(batch.GetBuffer(), 0, (int)batch.Length); }
            catch { if (Active) ui.BeginInvoke(() => Stop(null, tellOther: false)); return; }
        }
    }

    void ReadLoop()
    {
        var stream = net;
        try
        {
            while (stream != null)
            {
                int kind = stream.ReadByte();
                if (kind < 0) break;
                if (kind == 6)
                {
                    var f = new byte[5];
                    stream.ReadExactly(f);
                    float pos = BinaryPrimitives.ReadSingleLittleEndian(f.AsSpan(1));
                    ui.BeginInvoke(() => Stop(pos, tellOther: false));
                }
                else if (kind == 7)
                {
                    var len = new byte[4];
                    stream.ReadExactly(len);
                    var text = new byte[Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(len), 16u << 20)];
                    stream.ReadExactly(text);
                    var s = Encoding.UTF8.GetString(text);
                    ui.BeginInvoke(() => { try { if (s.Length > 0) Clipboard.SetText(s); } catch { } });
                }
                else break;
            }
        }
        catch { }
        if (Active) ui.BeginInvoke(() => Stop(null, tellOther: false));
    }

    // ---------- hiding this PC's cursor ----------

    /// <summary>A nearly invisible window under the waiting cursor whose cursor is blank, so nothing shows here.</summary>
    void ShowBlankCursor()
    {
        if (blank == null)
        {
            using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            blank = new QuietForm
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = true,
                Opacity = 0.01,
                BackColor = Color.Black,
                Size = new Size(64, 64),
                Cursor = new Cursor(bmp.GetHicon()),
            };
        }
        blank.Location = new Point(park.X - 32, park.Y - 32);
        blank.Show();
    }

    /// <summary>A window that never takes the focus from whatever was active on this PC.</summary>
    sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } // WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW
        }
    }

    public void Dispose()
    {
        if (Active) Recall();
        blank?.Dispose();
        outboxReady.Dispose();
    }
}
