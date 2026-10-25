using System.Drawing.Drawing2D;

namespace Async;

static class Art
{
    /// <summary>The tray/taskbar icon: a purple portal ring with an orange core, drawn at runtime.</summary>
    public static Icon MakeIcon(int size = 64)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size;
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(s * 0.04f, s * 0.04f, s * 0.92f, s * 0.92f);
                using var bg = new PathGradientBrush(path)
                {
                    CenterColor = Color.FromArgb(255, 255, 170, 70),
                    SurroundColors = new[] { Color.FromArgb(255, 10, 14, 32) },
                    FocusScales = new PointF(0.1f, 0.1f),
                };
                g.FillPath(bg, path);
            }
            using var glow = new Pen(Color.FromArgb(120, 176, 92, 255), s * 0.16f);
            g.DrawEllipse(glow, s * 0.14f, s * 0.14f, s * 0.72f, s * 0.72f);
            using var ring = new Pen(Color.FromArgb(255, 200, 140, 255), s * 0.07f);
            g.DrawEllipse(ring, s * 0.14f, s * 0.14f, s * 0.72f, s * 0.72f);
            using var arc = new Pen(Color.White, s * 0.05f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, s * 0.14f, s * 0.14f, s * 0.72f, s * 0.72f, 200, 70);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
