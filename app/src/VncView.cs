using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Async;

/// <summary>
/// A small VNC (RFB 3.3/3.8) viewer, used for computers whose screen sharing speaks VNC: macOS
/// Screen Sharing, and VNC servers on Linux. Supports None and VNC-password security and the Raw,
/// CopyRect, ZRLE and DesktopSize encodings, scales the remote screen to fit, and sends mouse and
/// keyboard input back.
/// </summary>
sealed class VncView : Control
{
    const int Raw = 0, CopyRect = 1, Zrle = 16, DesktopSize = -223;

    readonly object fbLock = new();
    Bitmap? fb;
    TcpClient? client;
    NetworkStream? net;
    CancellationTokenSource? stop;
    // ZRLE's zlib stream lasts one connection; each connection starts a fresh one.
    ZlibFeed zfeed = new();
    ZLibStream? zlib;
    Task reader = Task.CompletedTask;
    int buttons;

    public string RemoteName { get; private set; } = "";
    public int Updates { get; private set; }

    /// <summary>A copy of the remote screen as received so far (for tests and docs).</summary>
    public Bitmap? Snapshot() { lock (fbLock) return fb == null ? null : new Bitmap(fb); }
    public bool IsConnected { get; private set; }
    /// <summary>Connecting or connected: don't start another connection.</summary>
    public bool IsBusy { get; private set; }
    public event Action? Connected;
    public event Action<string>? Disconnected;

    public VncView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = Color.Black;
        TabStop = true;
    }

    // ---------- connection ----------

    public void Connect(string host, int port, string? password)
    {
        Disconnect();
        stop = new CancellationTokenSource();
        var token = stop.Token;
        var previous = reader;
        IsBusy = true;
        reader = Task.Run(async () =>
        {
            string why = "The connection closed.";
            try
            {
                // The last connection's reader shares these fields; let it finish closing first.
                await previous.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ => { });
                zlib?.Dispose();
                zlib = null;
                zfeed = new ZlibFeed();
                client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(host, port, token);
                net = client.GetStream();
                Handshake(password);
                IsConnected = true;
                BeginInvoke(() => Connected?.Invoke());
                RequestUpdate(incremental: false);
                while (!token.IsCancellationRequested) ReadServerMessage();
            }
            catch (Exception e) when (e is not OperationCanceledException) { why = e is VncException ? e.Message : $"Screen sharing connection lost: {e.Message}"; }
            catch (OperationCanceledException) { }
            IsConnected = false;
            if (!token.IsCancellationRequested) IsBusy = false;
            if (!token.IsCancellationRequested && !IsDisposed) BeginInvoke(() => Disconnected?.Invoke(why));
        }, CancellationToken.None);
    }

    public void Disconnect()
    {
        stop?.Cancel();
        try { client?.Close(); } catch { }
        client = null;
        net = null;
        IsConnected = false;
        IsBusy = false;
    }

    void Handshake(string? password)
    {
        string server = Encoding.ASCII.GetString(ReadBytes(12));
        if (!server.StartsWith("RFB ")) throw new VncException("That isn't a VNC screen sharing server.");
        bool v38 = string.CompareOrdinal(server, "RFB 003.007") >= 0;
        Write(Encoding.ASCII.GetBytes(v38 ? "RFB 003.008\n" : "RFB 003.003\n"));

        int security;
        if (v38)
        {
            int count = ReadU8();
            if (count == 0) throw new VncException(ReadReason());
            var offered = ReadBytes(count);
            if (offered.Contains((byte)2) && !string.IsNullOrEmpty(password)) security = 2;
            else if (offered.Contains((byte)1)) security = 1;
            else if (offered.Contains((byte)2)) throw new VncException("This computer's screen sharing needs a VNC password. Set it with Password… in Async's settings.");
            else throw new VncException("This computer only allows Apple's own screen sharing login. On the Mac, open System Settings → General → Sharing → Screen Sharing (i), turn on \"VNC viewers may control screen with password\" and set a password, then save that password in Async.");
            Write([(byte)security]);
        }
        else security = (int)ReadU32();

        if (security == 2)
        {
            var challenge = ReadBytes(16);
            Write(VncAuth(password ?? "", challenge));
        }
        if (security == 0) throw new VncException(ReadReason());
        if (security == 2 || v38)
        {
            if (ReadU32() != 0) throw new VncException(v38 ? ReadReason() : "Wrong VNC password.");
        }

        Write([1]); // ClientInit: share the screen with anyone else watching
        int w = ReadU16(), h = ReadU16();
        ReadBytes(16); // server's pixel format; we replace it below
        RemoteName = Encoding.UTF8.GetString(ReadBytes((int)ReadU32()));
        ResizeFramebuffer(w, h);

        // 32-bit little-endian BGRX, which is exactly a Format32bppRgb bitmap row.
        var pf = new byte[20];
        pf[0] = 0;
        pf[4] = 32; pf[5] = 24; pf[6] = 0; pf[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(12), 255);
        pf[14] = 16; pf[15] = 8; pf[16] = 0;
        Write(pf);

        int[] encodings = [Zrle, CopyRect, Raw, DesktopSize];
        var enc = new byte[4 + 4 * encodings.Length];
        enc[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(enc.AsSpan(2), (ushort)encodings.Length);
        for (int i = 0; i < encodings.Length; i++) BinaryPrimitives.WriteInt32BigEndian(enc.AsSpan(4 + 4 * i), encodings[i]);
        Write(enc);
    }

    /// <summary>VNC's DES challenge-response: the password (8 bytes, each bit-reversed) encrypts the challenge.</summary>
    static byte[] VncAuth(string password, byte[] challenge)
    {
        var key = new byte[8];
        var pw = Encoding.Latin1.GetBytes(password);
        for (int i = 0; i < 8 && i < pw.Length; i++)
        {
            byte b = pw[i], r = 0;
            for (int bit = 0; bit < 8; bit++) if ((b & (1 << bit)) != 0) r |= (byte)(0x80 >> bit);
            key[i] = r;
        }
#pragma warning disable SYSLIB0021 // DES is what the VNC protocol specifies
        using var des = DES.Create();
#pragma warning restore SYSLIB0021
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        try
        {
            des.Key = key;
            return des.EncryptEcb(challenge, PaddingMode.None);
        }
        catch (CryptographicException)
        {
            throw new VncException("That VNC password can't be used (it makes a weak DES key). Pick a different one.");
        }
    }

    // ---------- server messages ----------

    void ReadServerMessage()
    {
        switch (ReadU8())
        {
            case 0: ReadUpdate(); RequestUpdate(incremental: true); break;
            case 1: ReadBytes(1); ReadU16(); ReadBytes(6 * ReadU16()); break; // colour map: not used in true colour
            case 2: break; // bell
            case 3: // the remote clipboard changed
                ReadBytes(3);
                var text = Encoding.Latin1.GetString(ReadBytes((int)ReadU32()));
                BeginInvoke(() => { try { if (text.Length > 0) Clipboard.SetText(text); } catch { } });
                break;
            default: throw new VncException("The screen sharing server sent something Async doesn't understand.");
        }
    }

    void ReadUpdate()
    {
        ReadBytes(1);
        int rects = ReadU16();
        var dirty = Rectangle.Empty;
        for (int i = 0; i < rects; i++)
        {
            int x = ReadU16(), y = ReadU16(), w = ReadU16(), h = ReadU16();
            int encoding = ReadS32();
            switch (encoding)
            {
                case Raw: DecodeRaw(x, y, w, h); break;
                case CopyRect: DecodeCopyRect(x, y, w, h); break;
                case Zrle: DecodeZrle(x, y, w, h); break;
                case DesktopSize: ResizeFramebuffer(w, h); dirty = new Rectangle(0, 0, w, h); continue;
                default: throw new VncException($"Unsupported VNC encoding {encoding}.");
            }
            var r = new Rectangle(x, y, w, h);
            dirty = dirty.IsEmpty ? r : Rectangle.Union(dirty, r);
        }
        Updates++;
        if (!dirty.IsEmpty && IsHandleCreated) BeginInvoke(() => Invalidate(ToScreen(dirty)));
    }

    void DecodeRaw(int x, int y, int w, int h)
    {
        var row = new byte[w * 4];
        for (int j = 0; j < h; j++)
        {
            ReadInto(row);
            WriteRow(x, y + j, row, 0, w);
        }
    }

    void DecodeCopyRect(int x, int y, int w, int h)
    {
        int sx = ReadU16(), sy = ReadU16();
        lock (fbLock)
        {
            if (fb == null) return;
            using var copy = fb.Clone(new Rectangle(sx, sy, w, h), fb.PixelFormat);
            using var g = Graphics.FromImage(fb);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImageUnscaled(copy, x, y);
        }
    }

    /// <summary>ZRLE: one zlib stream for the whole connection, 64×64 tiles, 3-byte pixels.</summary>
    void DecodeZrle(int x, int y, int w, int h)
    {
        int length = (int)ReadU32();
        zfeed.Append(ReadBytes(length));
        zlib ??= new ZLibStream(zfeed, CompressionMode.Decompress);
        var tile = new uint[64 * 64];
        var palette = new uint[128];
        var rowBytes = new byte[64 * 4];

        for (int ty = y; ty < y + h; ty += 64)
        for (int tx = x; tx < x + w; tx += 64)
        {
            int tw = Math.Min(64, x + w - tx), th = Math.Min(64, y + h - ty), n = tw * th;
            int sub = ZByte();
            if (sub == 0) for (int i = 0; i < n; i++) tile[i] = ZPixel();
            else if (sub == 1) { uint c = ZPixel(); Array.Fill(tile, c, 0, n); }
            else if (sub <= 16)
            {
                for (int i = 0; i < sub; i++) palette[i] = ZPixel();
                int bits = sub == 2 ? 1 : sub <= 4 ? 2 : 4;
                for (int j = 0; j < th; j++)
                {
                    int byteVal = 0, left = 0;
                    for (int i = 0; i < tw; i++)
                    {
                        if (left == 0) { byteVal = ZByte(); left = 8; }
                        left -= bits;
                        tile[j * tw + i] = palette[(byteVal >> left) & ((1 << bits) - 1)];
                    }
                }
            }
            else if (sub == 128)
            {
                for (int i = 0; i < n;)
                {
                    uint c = ZPixel();
                    int run = ZRun();
                    for (int k = 0; k < run && i < n; k++) tile[i++] = c;
                }
            }
            else if (sub >= 130)
            {
                int size = sub - 128;
                for (int i = 0; i < size; i++) palette[i] = ZPixel();
                for (int i = 0; i < n;)
                {
                    int b = ZByte();
                    uint c = palette[b & 127];
                    int run = (b & 128) != 0 ? ZRun() : 1;
                    for (int k = 0; k < run && i < n; k++) tile[i++] = c;
                }
            }
            else throw new VncException($"Bad ZRLE tile type {sub}.");

            for (int j = 0; j < th; j++)
            {
                Buffer.BlockCopy(tile, j * tw * 4, rowBytes, 0, tw * 4);
                WriteRow(tx, ty + j, rowBytes, 0, tw);
            }
        }
    }

    int ZByte()
    {
        int b = zlib!.ReadByte();
        if (b < 0) throw new VncException("Screen data ended early.");
        return b;
    }

    uint ZPixel() => (uint)(ZByte() | ZByte() << 8 | ZByte() << 16);

    int ZRun()
    {
        int run = 1, b;
        do { b = ZByte(); run += b; } while (b == 255);
        return run;
    }

    // ---------- framebuffer ----------

    void ResizeFramebuffer(int w, int h)
    {
        lock (fbLock)
        {
            fb?.Dispose();
            fb = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppRgb);
        }
    }

    void WriteRow(int x, int y, byte[] src, int offset, int pixels)
    {
        lock (fbLock)
        {
            if (fb == null || y < 0 || y >= fb.Height || x < 0) return;
            pixels = Math.Min(pixels, fb.Width - x);
            if (pixels <= 0) return;
            var data = fb.LockBits(new Rectangle(x, y, pixels, 1), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            Marshal.Copy(src, offset, data.Scan0, pixels * 4);
            fb.UnlockBits(data);
        }
    }

    Rectangle Fit()
    {
        var size = fb?.Size ?? Size.Empty;
        if (size.IsEmpty || Width == 0 || Height == 0) return ClientRectangle;
        float scale = Math.Min((float)Width / size.Width, (float)Height / size.Height);
        int w = (int)(size.Width * scale), h = (int)(size.Height * scale);
        return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
    }

    Rectangle ToScreen(Rectangle r)
    {
        var fit = Fit();
        if (fb == null) return ClientRectangle;
        float sx = (float)fit.Width / fb.Width, sy = (float)fit.Height / fb.Height;
        return Rectangle.Inflate(new Rectangle(fit.X + (int)(r.X * sx), fit.Y + (int)(r.Y * sy), (int)(r.Width * sx) + 1, (int)(r.Height * sy) + 1), 2, 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        lock (fbLock)
        {
            if (fb == null) return;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            e.Graphics.DrawImage(fb, Fit());
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    // ---------- input ----------

    Point ToRemote(Point p)
    {
        var fit = Fit();
        if (fb == null || fit.Width == 0) return Point.Empty;
        int x = (int)((p.X - fit.X) * (float)fb.Width / fit.Width), y = (int)((p.Y - fit.Y) * (float)fb.Height / fit.Height);
        return new Point(Math.Clamp(x, 0, fb.Width - 1), Math.Clamp(y, 0, fb.Height - 1));
    }

    void SendPointer(Point p, int mask)
    {
        if (!IsConnected) return;
        var r = ToRemote(p);
        var msg = new byte[6];
        msg[0] = 5;
        msg[1] = (byte)mask;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)r.X);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4), (ushort)r.Y);
        TryWrite(msg);
    }

    static int Bit(MouseButtons b) => b switch { MouseButtons.Left => 1, MouseButtons.Middle => 2, MouseButtons.Right => 4, _ => 0 };

    protected override void OnMouseMove(MouseEventArgs e) => SendPointer(e.Location, buttons);
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); buttons |= Bit(e.Button); SendPointer(e.Location, buttons); }
    protected override void OnMouseUp(MouseEventArgs e) { buttons &= ~Bit(e.Button); SendPointer(e.Location, buttons); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int wheel = e.Delta > 0 ? 8 : 16;
        for (int i = 0; i < Math.Max(1, Math.Abs(e.Delta) / 120); i++)
        {
            SendPointer(e.Location, buttons | wheel);
            SendPointer(e.Location, buttons);
        }
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Keep Tab, arrows and Alt combinations for the remote computer instead of WinForms.
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        if (msg.Msg is WM_KEYDOWN or WM_SYSKEYDOWN) { OnKeyDown(new KeyEventArgs(keyData)); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e) { SendKey(e.KeyCode, true); e.Handled = true; }
    protected override void OnKeyUp(KeyEventArgs e) { SendKey(e.KeyCode, false); e.Handled = true; }

    void SendKey(Keys key, bool down)
    {
        uint sym = KeySym(key);
        if (sym == 0 || !IsConnected) return;
        var msg = new byte[8];
        msg[0] = 4;
        msg[1] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), sym);
        TryWrite(msg);
    }

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>Windows key → X11 keysym. Characters go unshifted; Shift travels as its own key.</summary>
    static uint KeySym(Keys k) => k switch
    {
        Keys.Back => 0xff08, Keys.Tab => 0xff09, Keys.Enter => 0xff0d, Keys.Escape => 0xff1b,
        Keys.Delete => 0xffff, Keys.Insert => 0xff63, Keys.Home => 0xff50, Keys.End => 0xff57,
        Keys.PageUp => 0xff55, Keys.PageDown => 0xff56,
        Keys.Left => 0xff51, Keys.Up => 0xff52, Keys.Right => 0xff53, Keys.Down => 0xff54,
        Keys.ShiftKey or Keys.LShiftKey => 0xffe1, Keys.RShiftKey => 0xffe2,
        Keys.ControlKey or Keys.LControlKey => 0xffe3, Keys.RControlKey => 0xffe4,
        Keys.Menu or Keys.LMenu => 0xffe9, Keys.RMenu => 0xffea,
        Keys.LWin => 0xffeb, Keys.RWin => 0xffec, Keys.Apps => 0xff67,
        Keys.CapsLock => 0xffe5, Keys.Space => 0x20,
        >= Keys.F1 and <= Keys.F12 => 0xffbe + (uint)(k - Keys.F1),
        >= Keys.NumPad0 and <= Keys.NumPad9 => 0xffb0 + (uint)(k - Keys.NumPad0),
        Keys.Multiply => 0xffaa, Keys.Add => 0xffab, Keys.Subtract => 0xffad, Keys.Decimal => 0xffae, Keys.Divide => 0xffaf,
        >= Keys.A and <= Keys.Z => (uint)('a' + (k - Keys.A)),
        _ => CharFor(k),
    };

    static uint CharFor(Keys k)
    {
        uint c = MapVirtualKey((uint)k, 2) & 0x7fff; // MAPVK_VK_TO_CHAR
        return c is >= 0x20 and < 0x100 ? char.ToLowerInvariant((char)c) : 0u;
    }

    void RequestUpdate(bool incremental)
    {
        var size = fb?.Size ?? Size.Empty;
        var msg = new byte[10];
        msg[0] = 3;
        msg[1] = (byte)(incremental ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6), (ushort)size.Width);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8), (ushort)size.Height);
        Write(msg);
    }

    // ---------- wire helpers ----------

    readonly object writeLock = new();
    void Write(byte[] data) { lock (writeLock) net!.Write(data); }
    void TryWrite(byte[] data) { try { Write(data); } catch { } }

    byte[] ReadBytes(int n) { var b = new byte[n]; ReadInto(b); return b; }
    void ReadInto(byte[] b) => net!.ReadExactly(b, 0, b.Length);
    int ReadU8() { int b = net!.ReadByte(); if (b < 0) throw new VncException("The screen sharing connection closed."); return b; }
    int ReadU16() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));
    uint ReadU32() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));
    int ReadS32() => BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));
    string ReadReason() => Encoding.UTF8.GetString(ReadBytes((int)ReadU32()));

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Disconnect(); fb?.Dispose(); zlib?.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Feeds ZRLE's compressed chunks to one long-lived ZLibStream. Reads only what has arrived,
    /// so the decompressor waits for the next chunk instead of seeing the end of the stream.
    /// </summary>
    sealed class ZlibFeed : Stream
    {
        readonly Queue<byte[]> chunks = new();
        int pos;

        public void Append(byte[] chunk) { if (chunk.Length > 0) chunks.Enqueue(chunk); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (chunks.Count == 0) return 0;
            var head = chunks.Peek();
            int n = Math.Min(count, head.Length - pos);
            Buffer.BlockCopy(head, pos, buffer, offset, n);
            pos += n;
            if (pos == head.Length) { chunks.Dequeue(); pos = 0; }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

sealed class VncException(string message) : Exception(message);
