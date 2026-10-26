using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Numerics;
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
    // Pseudo-encodings: the cursor's shape sent apart from the screen (so it's drawn here and moves
    // without waiting for frames), and the server sending changes without being asked for each one.
    const int CursorShape = -239, Fence = -312, ContinuousUpdates = -313;

    readonly object fbLock = new();
    // The screen lives in a plain pixel array the decoders write straight into; the bitmap only
    // wraps that memory for drawing. (A GDI+ call per row is what made updates slow.)
    uint[] pixels = [];
    GCHandle pin;
    Bitmap? fb;
    int fbW, fbH;
    Size FbSize => new(fbW, fbH);
    TcpClient? client;
    NetworkStream? net;
    CancellationTokenSource? stop;
    // ZRLE's zlib stream lasts one connection; each connection starts a fresh one.
    ZlibFeed zfeed = new();
    ZLibStream? zlib;
    readonly byte[] zbuf = new byte[1 << 16];
    int zpos, zlen;
    // The server keeps sending changes without being asked each time (the ContinuousUpdates extension).
    bool continuous;
    // What arrives, written to the log every few seconds while connected.
    long statBytes;
    int statUpdates;
    readonly HashSet<int> statEncodings = [];
    readonly System.Diagnostics.Stopwatch statClock = new();
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

    /// <summary>
    /// Connects and signs in: with the computer's own user name and password where it offers that
    /// (a Mac's Screen Sharing), otherwise with a VNC password, or with none if it asks for none.
    /// </summary>
    public void Connect(string host, int port, string? user, string? password)
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
                zpos = zlen = 0;
                continuous = false;
                statClock.Reset();
                (statUpdates, statBytes) = (0, 0);
                statEncodings.Clear();
                client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(host, port, token);
                net = client.GetStream();
                Handshake(user, password);
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

    void Handshake(string? user, string? password)
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
            bool hasUser = !string.IsNullOrEmpty(user), hasPassword = !string.IsNullOrEmpty(password);
            // Apple's own sign-in (a Mac user name and password) first: newer macOS often turns the
            // old VNC password away even when it's switched on.
            if (offered.Contains((byte)AppleAuth) && hasUser && hasPassword) security = AppleAuth;
            else if (offered.Contains((byte)2) && hasPassword) security = 2;
            else if (offered.Contains((byte)1)) security = 1;
            else if (offered.Contains((byte)AppleAuth)) throw new VncException("This Mac needs its user name and password. In Async's settings, click Password… next to it and enter the name and password you log into the Mac with.");
            else if (offered.Contains((byte)2)) throw new VncException("This computer's screen sharing needs a password. Set it with Password… in Async's settings.");
            else throw new VncException("This computer's screen sharing uses a sign-in Async doesn't know.");
            Write([(byte)security]);
        }
        else security = (int)ReadU32();

        if (security == 2)
        {
            var challenge = ReadBytes(16);
            Write(VncAuth(password ?? "", challenge));
        }
        else if (security == AppleAuth) AppleSignIn(user!, password!);
        if (security == 0) throw new VncException(ReadReason());
        if (security is 2 or AppleAuth || v38)
        {
            if (ReadU32() != 0)
            {
                string why = v38 ? ReadReason() : "";
                throw new VncException(security == AppleAuth
                    ? $"The Mac turned down that user name and password{(why.Length > 0 ? $" ({why})" : "")}. Use the name and password you log into the Mac with: click Password… next to it in Async's settings."
                    : $"Wrong screen sharing password{(why.Length > 0 ? $" ({why})" : "")}. Enter it again with Password… in Async's settings.");
            }
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

        int[] encodings = [Zrle, CopyRect, Raw, DesktopSize, CursorShape, Fence, ContinuousUpdates];
        var enc = new byte[4 + 4 * encodings.Length];
        enc[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(enc.AsSpan(2), (ushort)encodings.Length);
        for (int i = 0; i < encodings.Length; i++) BinaryPrimitives.WriteInt32BigEndian(enc.AsSpan(4 + 4 * i), encodings[i]);
        Write(enc);
    }

    /// <summary>Security type 30, Apple's Diffie-Hellman sign-in with the Mac's user name and password.</summary>
    const int AppleAuth = 30;

    /// <summary>
    /// Apple's sign-in: agree on a key with Diffie-Hellman (the Mac sends the generator, prime and its
    /// public key), then send the user name and password (64 bytes each, NUL-terminated, padded with
    /// random bytes) encrypted with AES-128-ECB under MD5(shared secret), followed by our public key.
    /// </summary>
    void AppleSignIn(string user, string password)
    {
        var generator = ReadBytes(2);
        int keyLength = ReadU16();
        var prime = ReadBytes(keyLength);
        var theirPublic = ReadBytes(keyLength);

        var p = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        var g = new BigInteger(generator, isUnsigned: true, isBigEndian: true);
        var secret = new BigInteger(RandomNumberGenerator.GetBytes(keyLength), isUnsigned: true, isBigEndian: true) % p;
        var ourPublic = BigInteger.ModPow(g, secret, p);
        var shared = BigInteger.ModPow(new BigInteger(theirPublic, isUnsigned: true, isBigEndian: true), secret, p);

        byte[] Fixed(BigInteger v)
        {
            var b = v.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (b.Length >= keyLength) return b;
            var padded = new byte[keyLength];
            b.CopyTo(padded, keyLength - b.Length);
            return padded;
        }

        var credentials = RandomNumberGenerator.GetBytes(128);
        void Put(string text, int at)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            int n = Math.Min(bytes.Length, 63);
            Array.Copy(bytes, 0, credentials, at, n);
            credentials[at + n] = 0;
        }
        Put(user, 0);
        Put(password, 64);

        using var aes = Aes.Create();
        aes.Key = MD5.HashData(Fixed(shared));
        var encrypted = aes.EncryptEcb(credentials, PaddingMode.None);
        CryptographicOperations.ZeroMemory(credentials);
        Write([.. encrypted, .. Fixed(ourPublic)]);
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
            case 0: ReadUpdate(); break;
            case 1: ReadBytes(1); ReadU16(); ReadBytes(6 * ReadU16()); break; // colour map: not used in true colour
            case 2: break; // bell
            case 3: // the remote clipboard changed
                ReadBytes(3);
                var text = Encoding.Latin1.GetString(ReadBytes((int)ReadU32()));
                BeginInvoke(() => { try { if (text.Length > 0) Clipboard.SetText(text); } catch { } });
                break;
            case 150: // EndOfContinuousUpdates: the server can stream; turn it on (as noVNC does)
                if (!continuous) { continuous = true; EnableContinuous(); }
                break;
            case 248: // a fence: answer it with the same payload (we handle messages strictly in order)
                ReadBytes(3);
                uint flags = ReadU32();
                var payload = ReadBytes(ReadU8());
                if ((flags & 0x80000000) != 0)
                {
                    var reply = new byte[9 + payload.Length];
                    reply[0] = 248;
                    BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), flags & 7);
                    reply[8] = (byte)payload.Length;
                    payload.CopyTo(reply, 9);
                    Write(reply);
                }
                break;
            default: throw new VncException("The screen sharing server sent something Async doesn't understand.");
        }
    }

    void ReadUpdate()
    {
        ReadBytes(1);
        int rects = ReadU16();
        // Ask for the next one straight away, so the server gathers changes while this one decodes.
        if (!continuous) RequestUpdate(incremental: true);
        var dirty = Rectangle.Empty;
        for (int i = 0; i < rects; i++)
        {
            int x = ReadU16(), y = ReadU16(), w = ReadU16(), h = ReadU16();
            int encoding = ReadS32();
            statEncodings.Add(encoding);
            switch (encoding)
            {
                case Raw: DecodeRaw(x, y, w, h); break;
                case CopyRect: DecodeCopyRect(x, y, w, h); break;
                case Zrle: DecodeZrle(x, y, w, h); break;
                case DesktopSize:
                    ResizeFramebuffer(w, h);
                    if (continuous) EnableContinuous();
                    dirty = new Rectangle(0, 0, w, h);
                    continue;
                case CursorShape: ReadCursor(x, y, w, h); continue;
                default: throw new VncException($"Unsupported VNC encoding {encoding}.");
            }
            var r = new Rectangle(x, y, w, h);
            dirty = dirty.IsEmpty ? r : Rectangle.Union(dirty, r);
        }
        Updates++;
        LogStats();
        if (!dirty.IsEmpty && IsHandleCreated) BeginInvoke(() => Invalidate(ToScreen(dirty)));
    }

    void EnableContinuous()
    {
        var msg = new byte[10];
        msg[0] = 150;
        msg[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6), (ushort)fbW);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8), (ushort)fbH);
        Write(msg);
    }

    // ---------- cursor ----------

    IntPtr cursorHandle;
    Bitmap? cursorImage;
    Point cursorHotspot;

    /// <summary>The remote cursor's picture (pixels, then a 1-bit mask) and hotspot; shown as this control's cursor.</summary>
    void ReadCursor(int hotX, int hotY, int w, int h)
    {
        var data = ReadBytes(w * h * 4);
        int stride = (w + 7) / 8;
        var mask = ReadBytes(stride * h);
        if (w == 0 || h == 0) return;
        var image = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bits = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var argb = new int[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                bool shown = (mask[j * stride + i / 8] & (0x80 >> (i % 8))) != 0;
                argb[j * w + i] = shown ? (int)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((j * w + i) * 4)) | 0xff000000) : 0;
            }
        Marshal.Copy(argb, 0, bits.Scan0, argb.Length);
        image.UnlockBits(bits);
        if (!IsHandleCreated) { image.Dispose(); return; }
        BeginInvoke(() =>
        {
            cursorImage?.Dispose();
            cursorImage = image;
            cursorHotspot = new Point(hotX, hotY);
            ApplyCursor();
        });
    }

    /// <summary>Makes a Windows cursor from the remote one, at the size the remote screen is shown.</summary>
    void ApplyCursor()
    {
        if (cursorImage == null || fbW == 0) return;
        float scale = (float)Fit().Width / fbW;
        int w = Math.Max(1, (int)Math.Round(cursorImage.Width * scale)), h = Math.Max(1, (int)Math.Round(cursorImage.Height * scale));
        using var sized = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(cursorImage, 0, 0, w, h);
        }
        IntPtr icon = sized.GetHicon();
        if (!GetIconInfo(icon, out var info)) { DestroyIcon(icon); return; }
        info.fIcon = false;
        info.xHotspot = (int)(cursorHotspot.X * scale);
        info.yHotspot = (int)(cursorHotspot.Y * scale);
        IntPtr made = CreateIconIndirect(ref info);
        DeleteObject(info.hbmMask);
        DeleteObject(info.hbmColor);
        DestroyIcon(icon);
        if (made == IntPtr.Zero) return;
        Cursor = new Cursor(made);
        if (cursorHandle != IntPtr.Zero) DestroyIcon(cursorHandle);
        cursorHandle = made;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("gdi32.dll")] static extern int StretchDIBits(IntPtr hdc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, IntPtr bits, ref BitmapInfoHeader info, uint usage, uint rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr old);

    [StructLayout(LayoutKind.Sequential)]
    struct IconInfo { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    void LogStats()
    {
        statUpdates++;
        if (!statClock.IsRunning) statClock.Start();
        double seconds = statClock.Elapsed.TotalSeconds;
        if (seconds < 5) return;
        Log.Write($"VNC {fbW}x{fbH}: {statUpdates / seconds:0.0} updates/s, {statBytes / seconds / 1e6:0.00} MB/s, encodings {string.Join(",", statEncodings)}{(continuous ? ", continuous" : "")}");
        (statUpdates, statBytes) = (0, 0);
        statClock.Restart();
    }

    void DecodeRaw(int x, int y, int w, int h)
    {
        var row = new byte[w * 4];
        for (int j = 0; j < h; j++)
        {
            ReadInto(row);
            lock (fbLock) WriteRow(x, y + j, MemoryMarshal.Cast<byte, uint>(row.AsSpan()));
        }
    }

    void DecodeCopyRect(int x, int y, int w, int h)
    {
        int sx = ReadU16(), sy = ReadU16();
        lock (fbLock)
        {
            w = Math.Min(w, Math.Min(fbW - sx, fbW - x));
            h = Math.Min(h, Math.Min(fbH - sy, fbH - y));
            if (w <= 0 || h <= 0) return;
            // Rows in the order that never overwrites one still to be copied (a span copy handles overlap within a row).
            bool down = y > sy;
            for (int k = 0; k < h; k++)
            {
                int j = down ? h - 1 - k : k;
                pixels.AsSpan((sy + j) * fbW + sx, w).CopyTo(pixels.AsSpan((y + j) * fbW + x, w));
            }
        }
    }

    /// <summary>ZRLE: one zlib stream for the whole connection, 64×64 tiles, 3-byte pixels.</summary>
    void DecodeZrle(int x, int y, int w, int h)
    {
        int length = (int)ReadU32();
        zfeed.Append(ReadBytes(length));
        zlib ??= new ZLibStream(zfeed, CompressionMode.Decompress);
        lock (fbLock) DecodeZrleTiles(x, y, w, h);
    }

    void DecodeZrleTiles(int x, int y, int w, int h)
    {
        var tile = new uint[64 * 64];
        var palette = new uint[128];

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

            for (int j = 0; j < th; j++) WriteRow(tx, ty + j, tile.AsSpan(j * tw, tw));
        }
    }

    int ZByte()
    {
        if (zpos == zlen)
        {
            zlen = Math.Max(0, zlib!.Read(zbuf, 0, zbuf.Length));
            zpos = 0;
            if (zlen == 0) throw new VncException("Screen data ended early.");
        }
        return zbuf[zpos++];
    }

    uint ZPixel()
    {
        if (zlen - zpos >= 3)
        {
            uint c = (uint)(zbuf[zpos] | zbuf[zpos + 1] << 8 | zbuf[zpos + 2] << 16);
            zpos += 3;
            return c;
        }
        return (uint)(ZByte() | ZByte() << 8 | ZByte() << 16);
    }

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
            FreeFramebuffer();
            (fbW, fbH) = (Math.Max(1, w), Math.Max(1, h));
            pixels = new uint[fbW * fbH];
            pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            fb = new Bitmap(fbW, fbH, fbW * 4, PixelFormat.Format32bppRgb, pin.AddrOfPinnedObject());
        }
    }

    void FreeFramebuffer()
    {
        fb?.Dispose();
        fb = null;
        if (pin.IsAllocated) pin.Free();
    }

    /// <summary>Copies one row of pixels into the screen, clipped to it. Call with fbLock held.</summary>
    void WriteRow(int x, int y, ReadOnlySpan<uint> src)
    {
        if (y < 0 || y >= fbH || x < 0 || x >= fbW) return;
        int n = Math.Min(src.Length, fbW - x);
        src[..n].CopyTo(pixels.AsSpan(y * fbW + x, n));
    }

    Rectangle Fit()
    {
        var size = FbSize;
        if (size.IsEmpty || Width == 0 || Height == 0) return ClientRectangle;
        float scale = Math.Min((float)Width / size.Width, (float)Height / size.Height);
        int w = (int)(size.Width * scale), h = (int)(size.Height * scale);
        return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
    }

    Rectangle ToScreen(Rectangle r)
    {
        var fit = Fit();
        if (fbW == 0) return ClientRectangle;
        float sx = (float)fit.Width / fbW, sy = (float)fit.Height / fbH;
        return Rectangle.Inflate(new Rectangle(fit.X + (int)(r.X * sx), fit.Y + (int)(r.Y * sy), (int)(r.Width * sx) + 1, (int)(r.Height * sy) + 1), 2, 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        lock (fbLock)
        {
            if (fb == null) return;
            // GDI's halftone stretch straight from the pixel array: smooth like GDI+'s high-quality
            // scaling, at a fraction of the cost (it runs on the thread that also handles the mouse).
            var fit = Fit();
            var info = new BitmapInfoHeader { biSize = 40, biWidth = fbW, biHeight = -fbH, biPlanes = 1, biBitCount = 32 };
            IntPtr hdc = e.Graphics.GetHdc();
            try
            {
                SetStretchBltMode(hdc, 4); // HALFTONE
                SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                StretchDIBits(hdc, fit.X, fit.Y, fit.Width, fit.Height, 0, 0, fbW, fbH, pin.AddrOfPinnedObject(), ref info, 0, 0x00CC0020);
            }
            finally { e.Graphics.ReleaseHdc(hdc); }
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyCursor();
        Invalidate();
    }

    // ---------- input ----------

    Point ToRemote(Point p)
    {
        var fit = Fit();
        if (fbW == 0 || fit.Width == 0) return Point.Empty;
        int x = (int)((p.X - fit.X) * (float)fbW / fit.Width), y = (int)((p.Y - fit.Y) * (float)fbH / fit.Height);
        return new Point(Math.Clamp(x, 0, fbW - 1), Math.Clamp(y, 0, fbH - 1));
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

    // Mouse moves go out at most every 17 ms, latest position only, as noVNC and TigerVNC do. A
    // 1000 Hz mouse would otherwise flood the server (a Mac handles each one before the next
    // screen update), which makes everything on the remote screen move in jumps.
    const int MoveInterval = 17;
    readonly System.Diagnostics.Stopwatch sinceMove = System.Diagnostics.Stopwatch.StartNew();
    System.Windows.Forms.Timer? moveTimer;
    Point? pendingMove;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        pendingMove = e.Location;
        if (sinceMove.ElapsedMilliseconds >= MoveInterval) { FlushMove(); return; }
        // The timer sends the last position if the mouse stops before the interval is up.
        if (moveTimer == null)
        {
            moveTimer = new System.Windows.Forms.Timer { Interval = MoveInterval };
            moveTimer.Tick += (_, _) => FlushMove();
        }
        if (!moveTimer.Enabled) moveTimer.Start();
    }

    /// <summary>Sends a held-back mouse move now (before a click, so the click lands where the pointer is).</summary>
    void FlushMove()
    {
        moveTimer?.Stop();
        if (pendingMove is not { } p) return;
        pendingMove = null;
        SendPointer(p, buttons);
        sinceMove.Restart();
    }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); FlushMove(); buttons |= Bit(e.Button); SendPointer(e.Location, buttons); }
    protected override void OnMouseUp(MouseEventArgs e) { FlushMove(); buttons &= ~Bit(e.Button); SendPointer(e.Location, buttons); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        FlushMove();
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
        var size = FbSize;
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
    void ReadInto(byte[] b) { net!.ReadExactly(b, 0, b.Length); statBytes += b.Length; }
    int ReadU8() { int b = net!.ReadByte(); if (b < 0) throw new VncException("The screen sharing connection closed."); return b; }
    int ReadU16() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));
    uint ReadU32() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));
    int ReadS32() => BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));
    string ReadReason() => Encoding.UTF8.GetString(ReadBytes((int)ReadU32()));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disconnect();
            lock (fbLock) FreeFramebuffer();
            zlib?.Dispose();
            cursorImage?.Dispose();
            moveTimer?.Dispose();
            if (cursorHandle != IntPtr.Zero) DestroyIcon(cursorHandle);
        }
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
