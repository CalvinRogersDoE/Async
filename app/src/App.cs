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
    readonly PortalForm portal;
    readonly CoreClient core;
    SoundPlayer soundIn, soundOut;
    readonly double slowmo = double.TryParse(Environment.GetEnvironmentVariable("ASYNC_SLOWMO"), out var f) && f > 0 ? f : 1;
    readonly ToolStripMenuItem startup;
    SettingsForm? settingsForm;
    bool busy;

    public AsyncApp(string[] args)
    {
        soundIn = SoundLibrary.Player(cfg, opening: true);
        soundOut = SoundLibrary.Player(cfg, opening: false);
        core = new CoreClient();
        portal = new PortalForm(cfg, core, icon);
        portal.StatusChanged += s => tray!.Text = Trim($"Async: {s}");
        edges = new EdgeWatcher(cfg);
        edges.PushedTop += OnPushTop;
        edges.PushedBottom += OnPushBottom;
        edges.Hotkey += OnHotkey;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Punch in here", null, (_, _) => OnHotkey());
        menu.Items.Add("Close portal", null, (_, _) => { if (portal.Visible) Run(Close(CursorX(), fromBottom: false)); });
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

    void OnPushTop(Screen s, Point p)
    {
        if (busy) return;
        if (!portal.Visible) Run(Open(s, p.X));
        else if (portal.ScreenName != s.DeviceName) Run(Move(s, p.X));
        else if (cfg.TopPushToggles) Run(Close(p.X, fromBottom: false));
    }

    void OnPushBottom(Screen s, Point p)
    {
        if (busy || !portal.Visible || portal.ScreenName != s.DeviceName || !cfg.BottomPushCloses) return;
        Run(Close(p.X, fromBottom: true));
    }

    void OnHotkey()
    {
        if (busy) return;
        int x = CursorX();
        if (portal.Visible) Run(Close(x, fromBottom: false));
        else Run(Open(Screen.FromPoint(Cursor.Position), x));
    }

    static int CursorX() => Cursor.Position.X;

    async void Run(Task work)
    {
        try { await work; } catch (Exception e) { Log.Write($"Portal error: {e}"); }
    }

    // ---------- animations ----------

    async Task Open(Screen s, int screenX)
    {
        busy = true;
        Native.timeBeginPeriod(1);
        try
        {
            var b = s.Bounds;
            var c = new PointF(Math.Clamp(screenX - b.Left, 0, b.Width), 0);
            float R = FarCorner(c, b.Size);
            const float vortexR = 46;
            var col = cfg.InColor;

            portal.ShowOn(s);
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

    async Task Close(int screenX, bool fromBottom)
    {
        busy = true;
        Native.timeBeginPeriod(1);
        try
        {
            var b = portal.Target;
            var c = new PointF(Math.Clamp(screenX - b.Left, 0, b.Width), fromBottom ? b.Height : 0);
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

    async Task Move(Screen s, int screenX)
    {
        await Close(portal.Target.Left + portal.Target.Width / 2, fromBottom: false);
        await Open(s, screenX);
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
            tray.ShowBalloonTip(3000, "Async", $"Push through the top of any screen to punch into {name}. {cfg.Hotkey} works too.", ToolTipIcon.None);
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
        portal.Disconnect();
        core.Dispose();
        edges.Dispose();
        fx.Dispose();
        tray.Dispose();
        base.ExitThreadCore();
    }
}
