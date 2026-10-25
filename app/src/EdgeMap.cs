using System.Drawing.Drawing2D;

namespace Async;

/// <summary>
/// The "Screen edges" picture in Settings: your monitors as Windows arranges them, with every
/// outer top and side edge clickable (the bottom is always the way back). Clicking an edge picks which paired computer pushing through it opens.
/// Edges joined to another monitor aren't drawn, since the mouse just crosses over there.
/// </summary>
sealed class EdgeMap : Control
{
    static readonly Color Monitor = Color.FromArgb(38, 34, 58), MonitorLine = Color.FromArgb(80, 72, 110),
        Unset = Color.FromArgb(70, 64, 96), Text1 = Color.FromArgb(235, 230, 250), Dim = Color.FromArgb(160, 150, 190);
    static readonly Color[] Palette =
    [
        Color.FromArgb(176, 92, 255), Color.FromArgb(46, 204, 170), Color.FromArgb(255, 154, 46),
        Color.FromArgb(255, 92, 160), Color.FromArgb(90, 160, 255), Color.FromArgb(240, 210, 70),
    ];
    const int Thick = 7;

    readonly Screen[] screens;
    readonly ToolTip tip = new();
    IReadOnlyList<Device> devices = [];
    (Screen Screen, Edge Edge)? hover;

    /// <summary>Edge key (<see cref="Settings.EdgeKey"/>) → device id.</summary>
    public Dictionary<string, string> Mapping { get; }

    public EdgeMap(Dictionary<string, string> mapping)
    {
        Mapping = mapping;
        // Left to right, then top to bottom, numbered the way they sit on the desk.
        screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 180;
        Cursor = Cursors.Hand;
    }

    public void SetDevices(IReadOnlyList<Device> list)
    {
        devices = list;
        Invalidate();
    }

    Color ColorOf(string id)
    {
        int i = devices.ToList().FindIndex(d => d.Id == id);
        return i < 0 ? Unset : Palette[i % Palette.Length];
    }

    string NameOf(string id) => devices.FirstOrDefault(d => d.Id == id)?.Name ?? "a removed device";

    string? DeviceAt(Screen s, Edge e) => Mapping.TryGetValue(Settings.EdgeKey(s.DeviceName, e), out var d) && d != "" ? d : null;

    static string Number(Screen s) => new string(s.DeviceName.Where(char.IsDigit).ToArray()) is { Length: > 0 } n ? n : "?";

    static string EdgeName(Edge e) => e.ToString().ToLowerInvariant();

    // ---------- layout ----------

    const int LegendHeight = 26;

    /// <summary>Monitor rectangles scaled into the control, keeping the room for edge bars and the legend.</summary>
    Dictionary<Screen, RectangleF> Placement()
    {
        var all = screens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
        float pad = 12, w = ClientSize.Width - pad * 2, h = ClientSize.Height - pad * 2 - LegendHeight;
        float k = Math.Min(w / all.Width, h / all.Height);
        float ox = pad + (w - all.Width * k) / 2, oy = pad + (h - all.Height * k) / 2;
        return screens.ToDictionary(s => s, s =>
        {
            var b = s.Bounds;
            // A small gap between monitors so each one reads as its own screen.
            return RectangleF.Inflate(new RectangleF(ox + (b.Left - all.Left) * k, oy + (b.Top - all.Top) * k, b.Width * k, b.Height * k), -3, -3);
        });
    }

    static RectangleF Bar(RectangleF r, Edge e)
    {
        float inset = Math.Min(r.Width, r.Height) * 0.08f;
        return e switch
        {
            Edge.Top => new(r.Left + inset, r.Top - Thick / 2f, r.Width - inset * 2, Thick),
            Edge.Bottom => new(r.Left + inset, r.Bottom - Thick / 2f, r.Width - inset * 2, Thick),
            Edge.Left => new(r.Left - Thick / 2f, r.Top + inset, Thick, r.Height - inset * 2),
            _ => new(r.Right - Thick / 2f, r.Top + inset, Thick, r.Height - inset * 2),
        };
    }

    (Screen, Edge)? HitTest(Point p)
    {
        foreach (var (s, r) in Placement())
            foreach (var e in Edges.Mappable)
                if (EdgeWatcher.IsFree(s.Bounds, e) && RectangleF.Inflate(Bar(r, e), 6, 6).Contains(p))
                    return (s, e);
        return null;
    }

    // ---------- drawing ----------

    protected override void OnPaint(PaintEventArgs pe)
    {
        var g = pe.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var big = new Font("Segoe UI Semibold", 15f);
        using var small = new Font("Segoe UI", 8f);

        foreach (var (s, r) in Placement())
        {
            using (var fill = new SolidBrush(Monitor)) g.FillRectangle(fill, r);
            using (var line = new Pen(MonitorLine)) g.DrawRectangle(line, r.X, r.Y, r.Width, r.Height);
            string label = Number(s), size = $"{s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " · main" : "")}";
            var ls = g.MeasureString(label, big);
            var ss = g.MeasureString(size, small);
            float cy = r.Top + r.Height / 2;
            using (var t = new SolidBrush(Text1)) g.DrawString(label, big, t, r.Left + (r.Width - ls.Width) / 2, cy - ls.Height + 4);
            using (var t = new SolidBrush(Dim)) g.DrawString(size, small, t, r.Left + (r.Width - ss.Width) / 2, cy + 4);

            foreach (var e in Edges.Mappable)
            {
                if (!EdgeWatcher.IsFree(s.Bounds, e)) continue;
                var bar = Bar(r, e);
                string? device = DeviceAt(s, e);
                bool hot = hover is { } h && h.Screen == s && h.Edge == e;
                var col = device != null ? ColorOf(device) : Unset;
                if (hot) col = PortalFx.Lerp(col, Color.White, 0.35f);
                using var brush = new SolidBrush(col);
                using var path = Rounded(bar, Thick / 2f);
                g.FillPath(brush, path);
                if (device == null)
                {
                    // Dashed look for "nothing here yet".
                    using var gaps = new Pen(Monitor, 2) { DashPattern = [3, 3] };
                    if (e.Horizontal()) g.DrawLine(gaps, bar.Left, bar.Top + bar.Height / 2, bar.Right, bar.Top + bar.Height / 2);
                    else g.DrawLine(gaps, bar.Left + bar.Width / 2, bar.Top, bar.Left + bar.Width / 2, bar.Bottom);
                }
            }
        }

        // Legend: which colour is which computer.
        float x = 18, y = ClientSize.Height - LegendHeight + 4;
        using (var t = new SolidBrush(Dim))
        {
            foreach (var (d, i) in devices.Select((d, i) => (d, i)))
            {
                using var dot = new SolidBrush(Palette[i % Palette.Length]);
                g.FillEllipse(dot, x, y + 4, 11, 11);
                g.DrawString(d.Name, Font, t, x + 15, y);
                x += 15 + g.MeasureString(d.Name, Font).Width + 16;
            }
            if (devices.Count == 0) g.DrawString("Pair a device to give edges something to open.", Font, t, x, y);
        }
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- interaction ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitTest(e.Location);
        if (h != hover)
        {
            hover = h;
            Invalidate();
            if (h is { } x)
            {
                string? d = DeviceAt(x.Item1, x.Item2);
                tip.SetToolTip(this, $"Screen {Number(x.Item1)}, {EdgeName(x.Item2)} edge: {(d != null ? $"opens {NameOf(d)}" : "does nothing")}. Click to change.");
            }
            else tip.SetToolTip(this, "");
        }
        Cursor = h != null ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = null;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (HitTest(e.Location) is not { } hit) return;
        var (s, edge) = hit;
        string key = Settings.EdgeKey(s.DeviceName, edge);
        string? current = DeviceAt(s, edge);

        var menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        menu.Items.Add(new ToolStripLabel($"Screen {Number(s)}, {EdgeName(edge)} edge opens:") { ForeColor = SystemColors.GrayText });
        foreach (var d in devices)
        {
            var item = new ToolStripMenuItem(d.Name) { Checked = d.Id == current };
            item.Click += (_, _) => { Mapping[key] = d.Id; Invalidate(); };
            menu.Items.Add(item);
        }
        var none = new ToolStripMenuItem("Nothing") { Checked = current == null };
        none.Click += (_, _) => { Mapping.Remove(key); Invalidate(); };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(none);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, e.Location);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tip.Dispose();
        base.Dispose(disposing);
    }
}
