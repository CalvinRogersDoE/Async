using System.Runtime.InteropServices;

namespace Async;

/// <summary>Hosts Windows' own Remote Desktop ActiveX control (mstscax.dll), no interop assembly needed.</summary>
sealed class RdpHost : AxHost
{
    public RdpHost(string clsid) : base(clsid) { }

    public static string? FindClsid()
    {
        // Newer ProgIDs can be registered without the DLL actually serving them, so try each one.
        for (int v = 13; v >= 1; v--)
        {
            if (Type.GetTypeFromProgID($"MsTscAx.MsTscAx.{v}") is not { } t) continue;
            try
            {
                var probe = Activator.CreateInstance(t);
                if (probe != null) Marshal.ReleaseComObject(probe);
                Log.Write($"Using Remote Desktop control MsTscAx.MsTscAx.{v}");
                return t.GUID.ToString();
            }
            catch { }
        }
        return Type.GetTypeFromProgID("MsTscAx.MsTscAx")?.GUID.ToString();
    }
}

/// <summary>
/// A borderless window that covers one monitor and shows the remote desktop. Its shape is a
/// window region, so the portal animation can open it as a growing circle.
/// </summary>
sealed class PortalForm : Form
{
    static readonly Guid RdpEvents = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"); // IMsTscAxEvents

    readonly Settings cfg;
    readonly CoreClient core;
    readonly RdpHost rdp;
    readonly VncView vnc = new() { Dock = DockStyle.Fill, Visible = false };
    readonly Label status;
    readonly System.Windows.Forms.Timer poll = new() { Interval = 500 };
    dynamic? ocx;
    bool eventsHooked, loggedIn, connecting, usingVnc;
    (int W, int H, int Scale) fitted;

    public Rectangle Target { get; private set; }
    public string? ScreenName { get; private set; }
    public bool Connected => usingVnc ? vnc.IsConnected : State == 1;
    public event Action<string>? StatusChanged;

    public PortalForm(Settings cfg, CoreClient core, Icon icon)
    {
        this.cfg = cfg;
        this.core = core;
        Text = "Async";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(6, 8, 18);
        ShowInTaskbar = true;

        var clsid = RdpHost.FindClsid() ?? throw new InvalidOperationException("Windows' Remote Desktop control (mstscax.dll) isn't registered.");
        rdp = new RdpHost(clsid) { Dock = DockStyle.Fill };
        status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(205, 170, 255),
            BackColor = BackColor,
            Font = new Font("Segoe UI", 26, FontStyle.Regular, GraphicsUnit.Pixel),
        };
        Controls.Add(rdp);
        Controls.Add(vnc);
        Controls.Add(status);
        vnc.Connected += () => { Log.Write($"VNC connected to {vnc.RemoteName}"); StatusChanged?.Invoke($"Connected to {TargetName}"); };
        vnc.Disconnected += why =>
        {
            Log.Write($"VNC disconnected: {why}");
            SetStatus($"{why}\n\nPush down out of the bottom to go back.");
            StatusChanged?.Invoke("Not connected");
        };
        status.BringToFront();
        poll.Tick += (_, _) => Poll();
        poll.Start();
    }

    int State
    {
        get { try { return ocx == null ? 0 : (int)ocx.Connected; } catch { return 0; } }
    }

    /// <summary>Puts the (still invisible) window over a monitor and makes sure a session is on its way.</summary>
    public void ShowOn(Screen s)
    {
        Target = s.Bounds;
        ScreenName = s.DeviceName;
        _ = Handle;
        SetCircle(Point.Empty, 0);
        if (!Visible) Show();
        Native.SetWindowPos(Handle, Native.HWND_TOP, Target.X, Target.Y, Target.Width, Target.Height, Native.SWP_SHOWWINDOW);
        Native.ForceForeground(Handle);
        EnsureSession();
    }

    public void SetCircle(PointF c, float r)
    {
        int ri = (int)Math.Ceiling(r);
        var rgn = ri <= 0
            ? Native.CreateRectRgn(0, 0, 0, 0)
            : Native.CreateEllipticRgn((int)c.X - ri, (int)c.Y - ri, (int)c.X + ri + 1, (int)c.Y + ri + 1);
        Native.SetWindowRgn(Handle, rgn, true); // the window owns the region now
    }

    public void ClearShape() => Native.SetWindowRgn(Handle, IntPtr.Zero, true);

    public void FocusSession()
    {
        Native.ForceForeground(Handle);
        if (Connected) { if (usingVnc) vnc.Focus(); else rdp.Focus(); }
    }

    public void HideAway()
    {
        Hide();
        if (!cfg.StayConnected) Disconnect();
    }

    public void Disconnect()
    {
        vnc.Disconnect();
        try { if (State != 0) ocx!.Disconnect(); } catch (Exception e) { Log.Write($"Disconnect: {e.Message}"); }
    }

    public void Reconnect()
    {
        Disconnect();
        BeginInvoke(() => { if (Visible) EnsureSession(); });
    }

    void EnsureSession()
    {
        try
        {
            ocx ??= rdp.GetOcx();
            HookEvents();
            if (!Connected) Connect();
            else if (!usingVnc) Fit();
        }
        catch (Exception e)
        {
            SetStatus($"Couldn't start Remote Desktop:\n{e.Message}");
            Log.Write($"EnsureSession: {e}");
        }
    }

    string TargetName => core.Devices.FirstOrDefault(d => d.Id == cfg.TargetDevice)?.Name ?? "the other PC";

    async void Connect()
    {
        if (connecting) return;
        if (!cfg.IsConfigured || core.Devices.All(d => d.Id != cfg.TargetDevice))
        {
            SetStatus("No device to punch into yet.\n\nRight-click the Async tray icon → Settings… and pair one.");
            return;
        }
        string device = cfg.TargetDevice, user = cfg.UserFor(device);
        var (savedUser, pass) = Creds.Read(Settings.CredentialTarget(device));
        if (!string.IsNullOrEmpty(savedUser)) user = savedUser;

        connecting = true;
        SetStatus($"Opening a portal to {TargetName}…");
        try
        {
            // The device says what its screen sharing speaks: Remote Desktop (Windows, Linux) or VNC (macOS).
            var (host, port, service) = await core.OpenScreen(device);
            Text = $"Async · {TargetName}";
            usingVnc = service == "vnc";
            rdp.Visible = !usingVnc;
            vnc.Visible = usingVnc;

            if (usingVnc)
            {
                vnc.Connect(host, port, pass);
                Log.Write($"Connecting to {TargetName} by VNC through the tunnel ({host}:{port})");
                return;
            }

            if (pass == null || string.IsNullOrEmpty(user))
            {
                SetStatus($"No sign-in saved for {TargetName} yet.\n\nRight-click the Async tray icon → Settings… → Password…");
                return;
            }
            // The tunnel is end-to-end encrypted and only reaches devices we paired with, so
            // Remote Desktop's own certificate check (always a name mismatch here) adds nothing.
            loggedIn = false;
            fitted = default;
            ocx!.Server = host;
            ocx.UserName = user;
            ocx.Domain = "";
            ocx.DesktopWidth = Target.Width;
            ocx.DesktopHeight = Target.Height;
            ocx.ColorDepth = 32;

            dynamic adv = ocx.AdvancedSettings9;
            adv.RDPPort = port;
            adv.ClearTextPassword = pass;
            Try(() => adv.EnableCredSspSupport = true);
            Try(() => adv.AuthenticationLevel = 0);
            Try(() => adv.SmartSizing = true);        // scale if the session size lags behind the monitor
            Try(() => adv.RedirectClipboard = true);
            Try(() => adv.EnableAutoReconnect = true);
            Try(() => adv.GrabFocusOnConnect = false);

            ocx.Connect();
            Log.Write($"Connecting to {TargetName} through the tunnel ({host}:{port}) as {user} at {Target.Width}x{Target.Height}");
        }
        catch (Exception e)
        {
            SetStatus($"Couldn't reach {TargetName}:\n{e.Message}\n\nIs Async running on it?");
            Log.Write($"Connect: {e}");
        }
        finally { connecting = false; }
    }

    /// <summary>Match the remote desktop to the monitor it's on (size and display scale).</summary>
    void Fit()
    {
        if (!loggedIn) return;
        int w = Target.Width, h = Target.Height;
        int scale = Native.ScalePercent(new Point(Target.X + w / 2, Target.Y + h / 2));
        if (fitted == (w, h, scale)) return;
        int device = scale >= 200 ? 180 : scale >= 150 ? 140 : 100;
        try
        {
            ocx!.UpdateSessionDisplaySettings((uint)w, (uint)h, (uint)(w * 254 / 960), (uint)(h * 254 / 960), 0u, (uint)scale, (uint)device);
            fitted = (w, h, scale);
            Log.Write($"Session resized to {w}x{h} @ {scale}%");
        }
        catch (Exception e) { Log.Write($"UpdateSessionDisplaySettings: {e.Message}"); }
    }

    void HookEvents()
    {
        if (eventsHooked || ocx == null) return;
        eventsHooked = true;
        try
        {
            object o = ocx!;
            ComEventsHelper.Combine(o, RdpEvents, 3, new Action(() => BeginInvoke(OnLoginComplete)));
            ComEventsHelper.Combine(o, RdpEvents, 4, new Action<int>(r => BeginInvoke(() => OnDisconnected(r))));
        }
        catch (Exception e) { Log.Write($"Couldn't hook RDP events (status falls back to polling): {e.Message}"); }
    }

    void OnLoginComplete()
    {
        loggedIn = true;
        Log.Write("Logged in");
        StatusChanged?.Invoke($"Connected to {TargetName}");
        Fit();
    }

    void OnDisconnected(int reason)
    {
        loggedIn = false;
        string why = $"code {reason}";
        try { why = (string)ocx!.GetErrorDescription((uint)reason, (uint)(int)ocx.ExtendedDisconnectReason); } catch { }
        Log.Write($"Disconnected: {reason} {why}");
        // 1 = local disconnect, 2 = remote disconnect by user/server, 3 = server-side disconnect.
        if (reason is 1 or 2 or 3) SetStatus($"Portal to {TargetName} is closed.\nPush through the top again to reopen it.");
        else SetStatus($"The portal collapsed:\n{why}\n\nPush down out of the bottom to go back.");
        StatusChanged?.Invoke($"Not connected ({why})");
    }

    void Poll()
    {
        bool show = !Connected;
        if (status.Visible != show) status.Visible = show;
        if (show) status.BringToFront();
    }

    void SetStatus(string text) => status.Text = text;

    static void Try(Action a)
    {
        try { a(); } catch (Exception e) { Log.Write($"RDP setting skipped: {e.Message}"); }
    }

    protected override void WndProc(ref Message m)
    {
        // Moving between monitors with different scaling: keep our exact pixel bounds.
        if (m.Msg == Native.WM_DPICHANGED)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOP, Target.X, Target.Y, Target.Width, Target.Height, Native.SWP_NOACTIVATE);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideAway(); return; }
        base.OnFormClosing(e);
    }
}
