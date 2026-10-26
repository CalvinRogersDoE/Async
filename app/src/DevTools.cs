using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Async;

/// <summary>
/// Developer tool behind <c>Async.exe --render-docs &lt;folder&gt;</c>: draws the README images
/// (the portal animation on mock desktops, the settings window) and the app icon, all off-screen.
/// </summary>
static class DevTools
{
    public static void RenderDocs(string folder)
    {
        Directory.CreateDirectory(folder);
        RenderPreview(Path.Combine(folder, "preview.png"));
        RenderSettings(Path.Combine(folder, "settings.png"));
        WriteIcon(Path.Combine(folder, "async.ico"));
    }

    // ---------- animation preview ----------

    static void RenderPreview(string path)
    {
        const int w = 640, h = 360, gap = 12;
        var cfg = new Settings();
        using var sheet = new Bitmap(w * 2 + gap * 3, h * 2 + gap * 3);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(12, 10, 20));
        var c = new PointF(w * 0.62f, 0);
        float R = MathF.Sqrt(c.X * c.X + h * h) + 40;

        (float Radius, Color Col, bool Vortex, string Caption)[] frames =
        [
            (-1, cfg.InColor, false, "1. Push into the top edge: it starts to glow"),
            (0, cfg.InColor, true, "2. Keep pushing and the portal tears open"),
            (R * 0.82f, cfg.InColor, false, "3. The other PC fills the screen"),
            (R * 0.45f, cfg.OutColor, false, "4. Push out the bottom to close it"),
        ];
        for (int i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            using var frame = new Bitmap(w, h);
            using (var fg = Graphics.FromImage(frame))
            {
                fg.SmoothingMode = SmoothingMode.AntiAlias;
                MockDesktop(fg, w, h, local: true);
                if (f.Radius > 0)
                {
                    using var clip = new GraphicsPath();
                    clip.AddEllipse(c.X - f.Radius, c.Y - f.Radius, f.Radius * 2, f.Radius * 2);
                    fg.SetClip(clip);
                    MockDesktop(fg, w, h, local: false);
                    fg.ResetClip();
                }
                using var fx = new PortalFx();
                if (f.Radius < 0)
                {
                    EdgeGlow.Paint(fg, c.X, 0, top: true, 0.7f, f.Col);
                }
                else if (f.Vortex)
                {
                    fx.Shockwave(fg, c, 150, f.Col, 0.6f);
                    fx.Vortex(fg, c, 34, f.Col, 1);
                    Burst(fx, c, 80, f.Col, -200);
                }
                else
                {
                    fx.Rim(fg, c, f.Radius, f.Col, 1);
                    Burst(fx, c, f.Radius, f.Col, i == 3 ? -300 : 380);
                }
                fx.Sparks(fg);
                Caption(fg, f.Caption, w, h);
            }
            g.DrawImage(frame, gap + i % 2 * (w + gap), gap + i / 2 * (h + gap));
        }
        sheet.Save(path, ImageFormat.Png);
    }

    static void Burst(PortalFx fx, PointF c, float r, Color col, float speed)
    {
        for (int k = 0; k < 6; k++) { fx.Emit(c, r, 18, col, speed); fx.StepSparks(0.03f); }
    }

    /// <summary>A plain stand-in desktop: wallpaper gradient, a few windows and a taskbar.</summary>
    static void MockDesktop(Graphics g, int w, int h, bool local)
    {
        var (top, bottom) = local
            ? (Color.FromArgb(40, 70, 120), Color.FromArgb(20, 30, 60))
            : (Color.FromArgb(30, 110, 90), Color.FromArgb(10, 45, 40));
        using (var wall = new LinearGradientBrush(new Rectangle(0, 0, w, h), top, bottom, 70f)) g.FillRectangle(wall, 0, 0, w, h);

        var windows = local
            ? new[] { new RectangleF(40, 50, 260, 170), new RectangleF(250, 120, 300, 190) }
            : new[] { new RectangleF(60, 70, 340, 210), new RectangleF(380, 40, 210, 150) };
        foreach (var r in windows)
        {
            using var body = new SolidBrush(Color.FromArgb(235, 245, 247, 250));
            using var bar = new SolidBrush(local ? Color.FromArgb(70, 90, 130) : Color.FromArgb(50, 120, 100));
            using var line = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
            g.FillRectangle(body, r);
            g.FillRectangle(bar, r.X, r.Y, r.Width, 18);
            for (int k = 0; k < 4; k++) g.FillRectangle(line, r.X + 14, r.Y + 34 + k * 22, r.Width * (0.75f - k * 0.12f), 8);
        }
        using (var task = new SolidBrush(Color.FromArgb(220, 16, 16, 24))) g.FillRectangle(task, 0, h - 26, w, 26);
        using var font = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        var label = local ? "This PC" : "Other PC";
        var size = g.MeasureString(label, font);
        g.DrawString(label, font, text, w - size.Width - 18, h - 26 - size.Height - 10);
    }

    static void Caption(Graphics g, string caption, int w, int h)
    {
        using var font = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        using var back = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        using var text = new SolidBrush(Color.White);
        var size = g.MeasureString(caption, font);
        g.FillRectangle(back, 12, h - 26 - size.Height - 18, size.Width + 16, size.Height + 8);
        g.DrawString(caption, font, text, 20, h - 26 - size.Height - 14);
    }

    // ---------- settings window ----------

    static void RenderSettings(string path)
    {
        using var icon = Art.MakeIcon();
        // A throwaway identity, so the picture shows a fresh install rather than this PC's devices.
        using var core = new CoreClient(Path.Combine(Path.GetTempPath(), "async-docs-core"));
        using var form = new SettingsForm(new Settings(), core, icon, startsWithWindows: false)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000),
            ShowInTaskbar = false,
        };
        form.Show();
        // Show the edge map doing something: two made-up computers on this PC's real monitors.
        if (Find<EdgeMap>(form) is { } map)
        {
            Device[] demo = [new("demo-desktop", "Desktop"), new("demo-laptop", "Laptop")];
            map.SetDevices(demo);
            form.ShowSampleDevices(demo);
            var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ToArray();
            map.Mapping[Settings.EdgeKey(screens[0].DeviceName, Edge.Top)] = demo[0].Id;
            map.Mapping[Settings.EdgeKey(screens[^1].DeviceName, screens.Length > 1 ? Edge.Right : Edge.Left)] = demo[1].Id;
        }
        foreach (var (page, file) in new[] { ("Screen edges", path), ("Devices", Path.ChangeExtension(path, null) + "-devices.png"), ("Push to punch", Path.ChangeExtension(path, null) + "-push.png"), ("Sounds", Path.ChangeExtension(path, null) + "-sounds.png") })
        {
            form.ShowPage(page);
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(file, ImageFormat.Png);
        }
    }

    static T? Find<T>(Control root) where T : Control =>
        root as T ?? root.Controls.Cast<Control>().Select(Find<T>).FirstOrDefault(c => c != null);

    // ---------- VNC check ----------

    /// <summary><c>--vnc-snapshot HOST PORT OUT.png [UPDATES]</c>: connect, wait for screen updates, save what arrived.</summary>
    public static int VncSnapshot(string host, int port, string path, int updates)
    {
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false, Size = new Size(1600, 1000) };
        var view = new VncView { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        string? error = null;
        view.Disconnected += why => error = why;
        form.Show();
        view.Connect(host, port, Environment.GetEnvironmentVariable("ASYNC_VNC_USER"), Environment.GetEnvironmentVariable("ASYNC_VNC_PASSWORD"));
        var deadline = DateTime.Now.AddSeconds(15);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (view.Updates < updates && error == null && DateTime.Now < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        double seconds = clock.Elapsed.TotalSeconds;
        if (Environment.GetEnvironmentVariable("ASYNC_VNC_MOUSETEST") == "1" && error == null)
        {
            // A 1000 Hz mouse moving for one second: the server should see about 60 moves, not 1000.
            var move = typeof(VncView).GetMethod("OnMouseMove", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var spam = System.Diagnostics.Stopwatch.StartNew();
            int moves = 0;
            for (; spam.ElapsedMilliseconds < 1000; moves++)
            {
                move.Invoke(view, [new MouseEventArgs(MouseButtons.None, 0, 100 + moves % 500, 100, 0)]);
                Application.DoEvents();
                while (spam.Elapsed.TotalMilliseconds < moves + 1) { } // 1 ms apart, like a 1000 Hz mouse
            }
            Console.Error.WriteLine($"{moves} mouse moves in 1 s");
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(10); }
        }
        if (error == null)
        {
            // How long one full redraw of the portal-sized view takes.
            using var canvas = new Bitmap(view.Width, view.Height);
            var paint = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) view.DrawToBitmap(canvas, view.ClientRectangle);
            Console.Error.WriteLine($"full redraw at {view.Width}x{view.Height}: {paint.Elapsed.TotalMilliseconds / 10:0.0} ms");
        }
        // ASYNC_VNC_RECONNECT=1: connect a second time on the same viewer, as the portal does.
        if (Environment.GetEnvironmentVariable("ASYNC_VNC_RECONNECT") == "1" && error == null)
        {
            int before = view.Updates;
            view.Connect(host, port, Environment.GetEnvironmentVariable("ASYNC_VNC_USER"), Environment.GetEnvironmentVariable("ASYNC_VNC_PASSWORD"));
            deadline = DateTime.Now.AddSeconds(15);
            while (view.Updates < before + updates && error == null && DateTime.Now < deadline) { Application.DoEvents(); Thread.Sleep(20); }
            updates += before;
        }
        using var shot = view.Snapshot();
        shot?.Save(path, ImageFormat.Png);
        Console.Error.WriteLine(error ?? $"{view.Updates} updates from {view.RemoteName} ({view.Updates / seconds:0.0} a second)");
        return error == null && view.Updates >= updates ? 0 : 1;
    }

    // ---------- mouse mode check ----------

    /// <summary>
    /// <c>--mouse-test HOST PORT</c>: hands the pointer to an input service (async-sharetest prints
    /// what it receives), feeds in moves, clicks, a wheel turn and keys without touching this PC's
    /// cursor, then pushes back out through the edge it went in by and reports where it came back.
    /// </summary>
    public static int MouseTest(string host, int port)
    {
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
        form.Show();
        var share = new MouseShare(new Settings(), null!, form);
        var s = Screen.PrimaryScreen!;
        var b = s.Bounds;
        Task.Run(() => share.StartAt(host, port, "test", s, Edge.Right, new Point(b.Right - 1, b.Top + (int)(b.Height * 0.3)), live: false)).GetAwaiter().GetResult();
        void Pump(int ms) { for (int i = 0; i < ms / 5; i++) { Application.DoEvents(); Thread.Sleep(5); } }
        IntPtr buf = Marshal.AllocHGlobal(64);
        void Mouse(int msg, int dx = 0, int dy = 0, int data = 0)
        {
            Marshal.StructureToPtr(new Native.MSLLHOOKSTRUCT { pt = new Native.POINT { X = share.Park.X + dx, Y = share.Park.Y + dy }, mouseData = (uint)(data << 16) }, buf, false);
            share.OnMouse(0, msg, buf);
        }
        void Key(int vk, int scan, bool down, bool extended = false)
        {
            Marshal.StructureToPtr(new Native.KBDLLHOOKSTRUCT { vkCode = vk, scanCode = scan, flags = (down ? 0 : 0x80) | (extended ? 1 : 0) }, buf, false);
            share.OnKey(0, down ? 0x100 : 0x101, buf);
        }
        for (int i = 0; i < 10; i++) Mouse(0x200, 5, 2);
        Pump(100);
        Mouse(0x201); Mouse(0x202);
        Mouse(0x20A, data: 120);
        Key(0x11, 0x1D, true); Key(0x43, 0x2E, true); Key(0x43, 0x2E, false); Key(0x11, 0x1D, false); // Ctrl+C
        Key(0x27, 0x4D, true, extended: true); Key(0x27, 0x4D, false, extended: true);                  // Right arrow
        Pump(200);
        for (int i = 0; i < 40 && share.Active; i++) { Mouse(0x200, -30, 0); Pump(20); }
        Pump(300);
        Marshal.FreeHGlobal(buf);
        Console.Error.WriteLine(share.Active ? "pointer never came back" : $"pointer came back at {share.LastReturn:0.000} along the edge");
        return !share.Active && share.LastReturn is > 0.2f and < 0.4f ? 0 : 1;
    }

    /// <summary>
    /// <c>--input-receive-test SECONDS</c>: listens for the input service like the app does, but in
    /// dry run, so each move, click and key is printed to stderr instead of reaching this PC.
    /// Point <c>--mouse-test 127.0.0.1 24850</c> (or another computer's sender) at it.
    /// </summary>
    public static int InputReceiveTest(int seconds)
    {
        Environment.SetEnvironmentVariable("ASYNC_INPUT_DRY_RUN", "1");
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
        form.Show();
        Console.Error.WriteLine($"virtual desktop {SystemInformation.VirtualScreen}; monitors {string.Join(", ", Screen.AllScreens.Select(s => $"{s.DeviceName} {s.Bounds}"))}");
        using var input = new InputReceive(form);
        bool was = false;
        var until = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < until)
        {
            Application.DoEvents();
            Thread.Sleep(10);
            if (input.Active != was) Console.Error.WriteLine((was = input.Active) ? "active: this PC's edges paused" : "inactive: this PC's edges back on");
        }
        return 0;
    }

    // ---------- icon ----------

    /// <summary>Writes a multi-size .ico (PNG-compressed entries) from the runtime-drawn icon.</summary>
    static void WriteIcon(string path)
    {
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var images = sizes.Select(s =>
        {
            using var icon = Art.MakeIcon(s);
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();

        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(images[i].Length); w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }
}
