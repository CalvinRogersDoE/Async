using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Async;

/// <summary>
/// A click-through, always-on-top, per-pixel-alpha window over one monitor that draws the
/// portal rim, vortex, shockwaves and sparks. Drawing goes straight into a DIB section that
/// UpdateLayeredWindow pushes to the screen each frame.
/// </summary>
sealed class PortalFx : NativeWindow, IDisposable
{
    struct Spark { public float X, Y, Vx, Vy, Life, Max, Size; public Color Color; }

    readonly List<Spark> sparks = new();
    readonly Random rng = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    Rectangle bounds;
    IntPtr memDc, dib, oldBmp;
    Bitmap? surface;
    double lastFrame;

    public PortalFx()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Async.Fx",
            Style = unchecked((int)0x80000000),                     // WS_POPUP
            ExStyle = 0x80000 | 0x20 | 0x8 | 0x80 | 0x08000000,     // LAYERED | TRANSPARENT | TOPMOST | TOOLWINDOW | NOACTIVATE
        });
    }

    public double Time => clock.Elapsed.TotalSeconds;

    public void Begin(Rectangle monitor)
    {
        if (monitor != bounds || surface == null) Allocate(monitor);
        sparks.Clear();
        lastFrame = Time;
        using (var g = Graphics.FromImage(surface!)) g.Clear(Color.Transparent);
        Present();
        Native.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, Native.SWP_NOACTIVATE);
    }

    public void End() => Native.ShowWindow(Handle, 0);

    /// <summary>Starts a frame: clears the surface and advances the sparks. Returns seconds since the last frame.</summary>
    public Graphics Frame(out float dt)
    {
        double now = Time;
        dt = (float)Math.Clamp(now - lastFrame, 0, 0.05);
        lastFrame = now;
        var g = Graphics.FromImage(surface!);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        StepSparks(dt);
        return g;
    }

    public void Present()
    {
        var dst = new Native.POINT { X = bounds.X, Y = bounds.Y };
        var src = new Native.POINT();
        var size = new Native.SIZE { cx = bounds.Width, cy = bounds.Height };
        var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
    }

    // ---------- drawing ----------

    /// <summary>The crackling energy ring at the edge of the portal.</summary>
    public void Rim(Graphics g, PointF c, float r, Color col, float energy)
    {
        if (r < 2 || energy <= 0) return;
        double t = Time;
        int n = Math.Clamp((int)(r * 0.5f), 64, 420);
        var pts = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            double a = i * Math.PI * 2 / n;
            double wobble = Math.Sin(a * 7 + t * 9) * 3.2 + Math.Sin(a * 13 - t * 15) * 2.1 + Math.Sin(a * 31 + t * 27) * 1.3;
            float rr = r + (float)(wobble * (0.6 + energy));
            pts[i] = new PointF(c.X + (float)Math.Cos(a) * rr, c.Y + (float)Math.Sin(a) * rr);
        }

        var hot = Lerp(col, Color.White, 0.55f);
        (float width, int alpha, Color color)[] passes =
        {
            (70, 18, col), (38, 45, col), (18, 110, col), (7, 200, hot), (2.5f, 255, Color.White),
        };
        foreach (var (width, alpha, color) in passes)
        {
            using var pen = new Pen(Color.FromArgb((int)(alpha * energy), color), width) { LineJoin = LineJoin.Round };
            g.DrawPolygon(pen, pts);
        }

        // Counter-rotating arcs just inside and outside the rim.
        for (int k = 0; k < 3; k++)
        {
            float spin = (float)(t * 140 + k * 120);
            DrawArc(g, c, r - 22, spin, 38, Color.FromArgb((int)(170 * energy), hot), 3);
            DrawArc(g, c, r + 26, -spin * 0.7f + 60, 24, Color.FromArgb((int)(110 * energy), col), 2);
        }
    }

    /// <summary>The swirling ball that forms at the punch point before the portal tears open.</summary>
    public void Vortex(Graphics g, PointF c, float r, Color col, float alpha)
    {
        if (r < 1 || alpha <= 0) return;
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(c.X - r * 1.6f, c.Y - r * 1.6f, r * 3.2f, r * 3.2f);
            using var glow = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb((int)(230 * alpha), Color.White),
                SurroundColors = new[] { Color.FromArgb(0, col) },
                FocusScales = new PointF(0.15f, 0.15f),
            };
            g.FillPath(glow, path);
        }
        double t = Time;
        for (int k = 0; k < 5; k++)
        {
            float spin = (float)(t * 520 + k * 72);
            DrawArc(g, c, r * (0.45f + k * 0.14f), spin, 70, Color.FromArgb((int)(220 * alpha), Lerp(col, Color.White, 0.4f)), 2.5f);
        }
    }

    public void Shockwave(Graphics g, PointF c, float r, Color col, float alpha, float width = 6)
    {
        if (r < 1 || alpha <= 0) return;
        using var pen = new Pen(Color.FromArgb((int)(200 * alpha), Lerp(col, Color.White, 0.3f)), width);
        g.DrawEllipse(pen, c.X - r, c.Y - r, r * 2, r * 2);
        using var soft = new Pen(Color.FromArgb((int)(60 * alpha), col), width * 4);
        g.DrawEllipse(soft, c.X - r, c.Y - r, r * 2, r * 2);
    }

    public void Flash(Graphics g, Color col, float alpha)
    {
        if (alpha <= 0) return;
        using var b = new SolidBrush(Color.FromArgb((int)(255 * alpha), col));
        g.FillRectangle(b, 0, 0, bounds.Width, bounds.Height);
    }

    // ---------- sparks ----------

    /// <summary>Throws sparks off a ring of radius r. speed &gt; 0 flies outward, &lt; 0 gets sucked in.</summary>
    public void Emit(PointF c, float r, int count, Color col, float speed)
    {
        for (int i = 0; i < count && sparks.Count < 600; i++)
        {
            double a = rng.NextDouble() * Math.PI * 2;
            float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
            float x = c.X + cos * r, y = c.Y + sin * r;
            if (!bounds.IsEmpty && (x < -20 || y < -20 || x > bounds.Width + 20 || y > bounds.Height + 20)) continue;
            float v = speed * (0.4f + (float)rng.NextDouble());
            float swirl = 120 * ((float)rng.NextDouble() - 0.5f);
            float life = 0.25f + (float)rng.NextDouble() * 0.45f;
            sparks.Add(new Spark
            {
                X = x, Y = y,
                Vx = cos * v - sin * swirl, Vy = sin * v + cos * swirl,
                Life = life, Max = life,
                Size = 1.5f + (float)rng.NextDouble() * 3.5f,
                Color = rng.Next(4) == 0 ? Color.White : Lerp(col, Color.White, (float)rng.NextDouble() * 0.5f),
            });
        }
    }

    public void Sparks(Graphics g)
    {
        foreach (var s in sparks)
        {
            float k = s.Life / s.Max;
            using var pen = new Pen(Color.FromArgb((int)(255 * k), s.Color), s.Size * k + 0.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, s.X, s.Y, s.X - s.Vx * 0.03f, s.Y - s.Vy * 0.03f);
        }
    }

    public void StepSparks(float dt)
    {
        for (int i = sparks.Count - 1; i >= 0; i--)
        {
            var s = sparks[i];
            s.Life -= dt;
            if (s.Life <= 0) { sparks.RemoveAt(i); continue; }
            s.X += s.Vx * dt;
            s.Y += s.Vy * dt;
            float drag = MathF.Pow(0.12f, dt);
            s.Vx *= drag;
            s.Vy *= drag;
            sparks[i] = s;
        }
    }

    // ---------- helpers ----------

    static void DrawArc(Graphics g, PointF c, float r, float start, float sweep, Color col, float width)
    {
        if (r < 2) return;
        using var pen = new Pen(col, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, c.X - r, c.Y - r, r * 2, r * 2, start, sweep);
    }

    public static Color Lerp(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    void Allocate(Rectangle b)
    {
        Free();
        bounds = b;
        var bmi = new Native.BITMAPINFOHEADER
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            Width = b.Width, Height = -b.Height, Planes = 1, BitCount = 32,
        };
        dib = Native.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        oldBmp = Native.SelectObject(memDc, dib);
        surface = new Bitmap(b.Width, b.Height, b.Width * 4, PixelFormat.Format32bppPArgb, bits);
    }

    void Free()
    {
        surface?.Dispose();
        surface = null;
        if (memDc != IntPtr.Zero) { Native.SelectObject(memDc, oldBmp); Native.DeleteDC(memDc); memDc = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }
    }

    public void Dispose()
    {
        Free();
        DestroyHandle();
    }
}

static class Anim
{
    /// <summary>Calls frame(t) with t going 0→1 over ms milliseconds, on the UI thread.</summary>
    public static Task Run(int ms, Action<double> frame)
    {
        var done = new TaskCompletionSource();
        var sw = Stopwatch.StartNew();
        var timer = new System.Windows.Forms.Timer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / Math.Max(1, ms));
            try { frame(t); } catch (Exception e) { Log.Write($"Animation frame: {e}"); t = 1; }
            if (t >= 1) { timer.Dispose(); done.TrySetResult(); }
        };
        frame(0);
        timer.Start();
        return done.Task;
    }

    public static float InOutCubic(double t) => (float)(t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2);
    public static float OutCubic(double t) => (float)(1 - Math.Pow(1 - t, 3));
    public static float InCubic(double t) => (float)(t * t * t);
    public static float OutBack(double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return (float)(1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2));
    }
}
