using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Async;

/// <summary>
/// The light that gathers on the screen edge while you push into it, brighter and wider the closer
/// the push is to opening the portal. A thin click-through layered window along the edge; it fades
/// out on its own when the pushing stops.
/// </summary>
sealed class EdgeGlow : NativeWindow, IDisposable
{
    const int Depth = 140;
    readonly System.Windows.Forms.Timer frame = new() { Interval = 15 };
    Rectangle strip;
    IntPtr memDc, dib, oldBmp;
    Bitmap? surface;
    Color color;
    float target, shown;
    int along;
    Edge edge;
    long lastPush;

    public EdgeGlow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Async.EdgeGlow",
            Style = unchecked((int)0x80000000),                     // WS_POPUP
            ExStyle = 0x80000 | 0x20 | 0x8 | 0x80 | 0x08000000,     // LAYERED | TRANSPARENT | TOPMOST | TOOLWINDOW | NOACTIVATE
        });
        frame.Tick += (_, _) => Step();
    }

    /// <summary>Called for every bit of push; <paramref name="progress"/> is 0–1.</summary>
    public void Push(Screen s, Point cursor, Edge e, float progress, Color col)
    {
        var b = s.Bounds;
        var want = e switch
        {
            Edge.Top => new Rectangle(b.X, b.Y, b.Width, Depth),
            Edge.Bottom => new Rectangle(b.X, b.Bottom - Depth, b.Width, Depth),
            Edge.Left => new Rectangle(b.X, b.Y, Depth, b.Height),
            _ => new Rectangle(b.Right - Depth, b.Y, Depth, b.Height),
        };
        if (want != strip || surface == null) Allocate(want);
        edge = e;
        color = col;
        along = e.Horizontal() ? cursor.X - b.X : cursor.Y - b.Y;
        target = progress;
        lastPush = Environment.TickCount64;
        if (!frame.Enabled)
        {
            Native.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, strip.X, strip.Y, strip.Width, strip.Height, Native.SWP_NOACTIVATE);
            frame.Start();
        }
    }

    /// <summary>The push fired (or was cancelled): drop the glow at once.</summary>
    public void Clear()
    {
        target = shown = 0;
        frame.Stop();
        Native.ShowWindow(Handle, 0);
    }

    void Step()
    {
        if (Environment.TickCount64 - lastPush > 120) target = 0;
        // Rise quickly with the push, fall away a little slower.
        shown += (target - shown) * (target > shown ? 0.45f : 0.18f);
        if (target == 0 && shown < 0.02f) { Clear(); return; }
        Draw();
    }

    void Draw()
    {
        using (var g = Graphics.FromImage(surface!))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Side edges: swap x and y, so the same glow lies along the side instead of the top.
            if (!edge.Horizontal()) g.Transform = new Matrix(0, 1, 1, 0, 0, 0);
            bool near = edge is Edge.Top or Edge.Left;
            Paint(g, along, near ? 0 : Depth, near, shown, color);
        }
        Present();
    }

    /// <summary>Draws the glow at strength <paramref name="k"/> (0–1) on an edge at <paramref name="edgeY"/>.</summary>
    public static void Paint(Graphics g, float x, float edgeY, bool top, float k, Color color)
    {
        float rx = 140 + 520 * k, ry = 30 + (Depth - 40) * k;

        // A soft pool of light spreading from the edge.
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(x - rx, edgeY - ry, rx * 2, ry * 2);
            using var pool = new PathGradientBrush(path)
            {
                CenterPoint = new PointF(x, edgeY),
                CenterColor = Color.FromArgb((int)(170 * k), PortalFx.Lerp(color, Color.White, 0.25f)),
                SurroundColors = [Color.FromArgb(0, color)],
            };
            g.FillPath(pool, path);
        }

        // A bright seam right on the edge, as if the screen is about to split there.
        float half = rx * 0.75f;
        using (var seam = new LinearGradientBrush(new PointF(x - half, 0), new PointF(x + half, 0), Color.Transparent, Color.Transparent))
        {
            seam.InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(0, color), Color.FromArgb((int)(255 * k), Color.White), Color.FromArgb(0, color)],
                Positions = [0f, 0.5f, 1f],
            };
            float h = 2 + 4 * k;
            g.FillRectangle(seam, x - half, top ? edgeY : edgeY - h, half * 2, h);
        }
    }

    void Present()
    {
        var dst = new Native.POINT { X = strip.X, Y = strip.Y };
        var src = new Native.POINT();
        var size = new Native.SIZE { cx = strip.Width, cy = strip.Height };
        var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
    }

    void Allocate(Rectangle r)
    {
        Free();
        strip = r;
        var bmi = new Native.BITMAPINFOHEADER
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            Width = r.Width, Height = -r.Height, Planes = 1, BitCount = 32,
        };
        dib = Native.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        oldBmp = Native.SelectObject(memDc, dib);
        surface = new Bitmap(r.Width, r.Height, r.Width * 4, PixelFormat.Format32bppPArgb, bits);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE);
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
        frame.Dispose();
        Free();
        DestroyHandle();
    }
}
