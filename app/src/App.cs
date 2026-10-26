using System.Diagnostics;
using System.Media;
using Microsoft.Win32;

namespace Async;

/// <summary>Tray app: wires edge pushes and the hotkey to the portal open/close animations.</summary>
sealed class AsyncApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly Settings cfg = Settings.Load();
    readonly Icon icon = Art.MakeIcon();
    readonly NotifyIcon tray;
    readonly EdgeWatcher edges;
    readonly PortalFx fx = new();
    readonly EdgeGlow glow = new();
    readonly PortalForm portal;
    readonly CoreClient core;
    readonly MouseShare share;
    SoundPlayer soundIn, soundOut;
    readonly double slowmo = double.TryParse(Environment.GetEnvironmentVariable("ASYNC_SLOWMO"), out var f) && f > 0 ? f : 1;
    readonly ToolStripMenuItem startup;
    SettingsForm? settingsForm;
    bool busy;
    /// <summary>The edge the open portal came through.</summary>
    Edge entry = Edge.Top;

    public AsyncApp(string[] args)
    {
        soundIn = SoundLibrary.Player(cfg, opening: true);
        soundOut = SoundLibrary.Player(cfg, opening: false);
        core = new CoreClient();
        portal = new PortalForm(cfg, core, icon);
        portal.StatusChanged += s => tray!.Text = Trim($"Async: {s}");
        edges = new EdgeWatcher(cfg);
        edges.Pushed += OnPush;
        edges.Hotkey += OnHotkey;
        edges.Pushing += OnPushing;
        share = new MouseShare(cfg, core, portal);
        edges.Paused = () => share.Active;
        share.Returned += _ => edges.Cooldown(600);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Punch in here", null, (_, _) => OnHotkey());
        menu.Items.Add("Close portal", null, (_, _) => { if (portal.Visible) Run(Close(entry, Cursor.Position)); });
        menu.Items.Add("Disconnect", null, (_, _) => portal.Disconnect());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pair a device…", null, (_, _) => PairDialog.ShowFor(core, icon));
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        startup = new ToolStripMenuItem("Start with Windows") { Checked = StartsWithWindows() };
        startup.Click += (_, _) => startup.Checked = ToggleStartup();
        menu.Items.Add(startup);
        menu.Items.Add("Restart Async", null, (_, _) => Application.Restart());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        tray = new NotifyIcon { Icon = icon, Text = "Async", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => OnHotkey();

        core.DevicesChanged += WelcomeOnce;

        Log.Write($"Started. Monitors: {string.Join(", ", Screen.AllScreens.Select(s => $"{s.DeviceName} {s.Bounds}"))}");
        if (args.Contains("--demo"))
        {
            var t = new System.Windows.Forms.Timer { Interval = 1200 };
            t.Tick += (_, _) => { t.Dispose(); OnHotkey(); };
            t.Start();
        }
    }

    // ---------- triggers ----------

    enum Act { None, Open, Close }

    /// <summary>
    /// What pushing through an edge does. A mapped edge opens its computer (switching computers if a
    /// portal to another one is open there); pushing out the bottom always closes the portal.
    /// </summary>
    (Act, string?) Decide(Screen s, Edge e)
    {
        string? device = cfg.DeviceFor(s.DeviceName, e);
        if (device != null && core.Devices.All(d => d.Id != device)) device = null;
        if (portal.Visible && portal.ScreenName == s.DeviceName)
        {
            if (e == Edge.Bottom) return (Act.Close, null);
            if (device != null && device != portal.Device) return (Act.Open, device);
            return (Act.None, null);
        }
        return device != null ? (Act.Open, device) : (Act.None, null);
    }

    /// <summary>Light up the edge while a push builds, but only where the push would actually do something.</summary>
    void OnPushing(Screen s, Edge e, Point p, float progress)
    {
        if (busy) return;
        var (act, _) = Decide(s, e);
        if (act != Act.None) glow.Push(s, p, e, progress, act == Act.Close ? cfg.OutColor : cfg.InColor);
    }

    void OnPush(Screen s, Edge e, Point p)
    {
        glow.Clear();
        if (busy) return;
        var (act, device) = Decide(s, e);
        if (act == Act.Open && !portal.Visible && cfg.IsMouseEdge(s.DeviceName, e))
        {
            Run(CrossOver(device!, s, e, p));
            return;
        }
        if (act == Act.Close) Run(Close(e, p));
        else if (act == Act.Open) Run(portal.Visible ? Switch(s, e, p, device!) : Open(s, e, p, device!));
    }

    /// <summary>Mouse mode: the pointer and keyboard go over to the other computer.</summary>
    async Task CrossOver(string device, Screen s, Edge e, Point p)
    {
        try { await share.Start(device, s, e, p); }
        catch (Exception ex)
        {
            Log.Write($"Mouse mode couldn't start: {ex.Message}");
            string name = core.Devices.FirstOrDefault(d => d.Id == device)?.Name ?? "that computer";
            tray.ShowBalloonTip(5000, "Async", $"Couldn't move the pointer to {name}. Is Async running there, and up to date?", ToolTipIcon.Warning);
            edges.Cooldown(1500);
        }
    }

    void OnHotkey()
    {
        if (share.Active) { share.Recall(); return; }
        if (busy) return;
        var p = Cursor.Position;
        if (portal.Visible) Run(Close(entry, p));
        else Run(Open(Screen.FromPoint(p), Edge.Top, p, cfg.TargetDevice != "" ? cfg.TargetDevice : core.Devices.FirstOrDefault()?.Id ?? ""));
    }

    async void Run(Task work)
    {
        try { await work; } catch (Exception e) { Log.Write($"Portal error: {e}"); }
    }

    // ---------- animations ----------

    async Task Open(Screen s, Edge e, Point at, string device)
    {
        busy = true;
        Native.timeBeginPeriod(1);
        try
        {
            var b = s.Bounds;
            var c = e.Point(b, at);
            float R = FarCorner(c, b.Size);
            const float vortexR = 46;
            var col = cfg.InColor;
            entry = e;

            portal.ShowOn(s, device);
            fx.Begin(b);
            if (cfg.Sound) soundIn.Play();

            const double charge = 0.24;
            await Anim.Run(Ms(cfg.OpenMs), t =>
            {
                using var g = fx.Frame(out _);
                if (t < charge)
                {
                    double u = t / charge;
                    fx.Shockwave(g, c, Anim.OutCubic(u) * 320, col, (float)(1 - u));
                    fx.Vortex(g, c, vortexR * Anim.OutBack(u), col, 1);
                    fx.Emit(c, vortexR * 2.4f, 6, col, -260);
                }
                else
                {
                    double u = (t - charge) / (1 - charge);
                    float r = vortexR + (R - vortexR) * Anim.InOutCubic(u);
                    portal.SetCircle(c, r);
                    fx.Vortex(g, c, vortexR * (float)(1 - u), col, (float)(1 - u));
                    fx.Rim(g, c, r, col, 1);
                    fx.Emit(c, r, 16, col, 420);
                }
                fx.Sparks(g);
                fx.Present();
            });

            portal.ClearShape();
            portal.FocusSession();
            // Land on the other PC right where you punched through: nudge the pointer just inside
            // the edge so Remote Desktop moves the remote cursor to the same spot.
            Native.SetCursorPos(b.Left + Math.Clamp((int)c.X, 4, b.Width - 5), b.Top + Math.Clamp((int)c.Y, 4, b.Height - 5));

            await Anim.Run(Ms(260), t =>
            {
                using var g = fx.Frame(out _);
                fx.Flash(g, col, (float)(0.22 * (1 - t)));
                fx.Sparks(g);
                fx.Present();
            });
        }
        finally
        {
            fx.End();
            Native.timeEndPeriod(1);
            edges.Cooldown(400);
            busy = false;
        }
    }

    async Task Close(Edge e, Point at)
    {
        busy = true;
        Native.timeBeginPeriod(1);
        try
        {
            var b = portal.Target;
            var c = e.Point(b, at);
            float R = FarCorner(c, b.Size);
            var col = cfg.OutColor;

            fx.Begin(b);
            if (cfg.Sound) soundOut.Play();

            await Anim.Run(Ms(cfg.CloseMs), t =>
            {
                using var g = fx.Frame(out _);
                float r = R * (1 - Anim.InCubic(t));
                portal.SetCircle(c, r);
                fx.Rim(g, c, r, col, 1);
                fx.Emit(c, r, 14, col, -380);
                fx.Sparks(g);
                fx.Present();
            });

            portal.HideAway();
            fx.Emit(c, 4, 70, col, 900);

            await Anim.Run(Ms(300), t =>
            {
                using var g = fx.Frame(out _);
                fx.Shockwave(g, c, Anim.OutCubic(t) * 240, col, (float)(1 - t), 4);
                fx.Vortex(g, c, 30 * (float)(1 - t), col, (float)(1 - t));
                fx.Sparks(g);
                fx.Present();
            });
        }
        finally
        {
            fx.End();
            Native.timeEndPeriod(1);
            edges.Cooldown(400);
            busy = false;
        }
    }

    /// <summary>Close the open portal (wherever it is) and open one through this edge instead.</summary>
    async Task Switch(Screen s, Edge e, Point at, string device)
    {
        var b = portal.Target;
        await Close(entry, new Point(b.Left + b.Width / 2, b.Top + b.Height / 2));
        await Open(s, e, at, device);
    }

    int Ms(int ms) => (int)(ms * slowmo);

    static float FarCorner(PointF c, Size size)
    {
        float Dist(float x, float y) => MathF.Sqrt((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y));
        return MathF.Max(MathF.Max(Dist(0, 0), Dist(size.Width, 0)), MathF.Max(Dist(0, size.Height), Dist(size.Width, size.Height))) + 40;
    }

    // ---------- menu actions ----------

    bool welcomed;

    /// <summary>First run (nothing paired yet) opens Settings; otherwise a short reminder of how it works.</summary>
    void WelcomeOnce()
    {
        if (welcomed) return;
        welcomed = true;
        if (core.Devices.Count == 0)
        {
            tray.ShowBalloonTip(6000, "Async", "Welcome! Pair this PC with another one running Async to get started.", ToolTipIcon.Info);
            OpenSettings();
        }
        else
        {
            var name = core.Devices.FirstOrDefault(d => d.Id == cfg.TargetDevice)?.Name ?? core.Devices[0].Name;
            tray.ShowBalloonTip(3000, "Async", cfg.EdgeDevices == null
                ? $"Push through the top of any screen to punch into {name}. {cfg.Hotkey} works too."
                : $"Push through a screen edge to punch into another computer. {cfg.Hotkey} opens {name}.", ToolTipIcon.None);
        }
    }

    void OpenSettings()
    {
        if (settingsForm is { IsDisposed: false }) { settingsForm.Activate(); return; }
        settingsForm = new SettingsForm(cfg, core, icon, StartsWithWindows());
        settingsForm.Saved += connectionChanged =>
        {
            if (settingsForm.StartWithWindows != StartsWithWindows()) ToggleStartup();
            startup.Checked = StartsWithWindows();
            if (connectionChanged) portal.Disconnect();
            soundIn = SoundLibrary.Player(cfg, opening: true);
            soundOut = SoundLibrary.Player(cfg, opening: false);
            Log.Write("Settings saved");
        };
        settingsForm.Show();
        Native.ForceForeground(settingsForm.Handle);
    }

    static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Async") != null;
    }

    static bool ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue("Async") != null) { key.DeleteValue("Async"); return false; }
        key.SetValue("Async", $"\"{Environment.ProcessPath}\"");
        return true;
    }

    static string Trim(string s) => s.Length <= 127 ? s : s[..127];

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        share.Dispose();
        portal.Disconnect();
        core.Dispose();
        edges.Dispose();
        fx.Dispose();
        glow.Dispose();
        tray.Dispose();
        base.ExitThreadCore();
    }
}
