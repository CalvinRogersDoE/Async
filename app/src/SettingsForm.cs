using System.Drawing.Drawing2D;

namespace Async;

/// <summary>
/// The settings window (tray → Settings…): a sidebar of sections on the left, the chosen section on
/// the right, Cancel/Save along the bottom. Changes apply when you press Save.
/// </summary>
sealed class SettingsForm : Form
{
    static readonly Color Bg = Color.FromArgb(18, 16, 28), Side = Color.FromArgb(13, 11, 21), Card = Color.FromArgb(28, 25, 42),
        Field = Color.FromArgb(38, 34, 56), Line = Color.FromArgb(52, 46, 74), Text1 = Color.FromArgb(235, 230, 250),
        Dim = Color.FromArgb(160, 150, 190), Accent = Color.FromArgb(176, 92, 255), AccentDark = Color.FromArgb(120, 60, 200);
    static readonly string[] Packs = [SoundLibrary.BuiltIn, SoundLibrary.Portal, SoundLibrary.Portal2, SoundLibrary.Custom];

    readonly Settings cfg;
    readonly CoreClient core;
    readonly Icon icon;
    readonly TextBox hotkey = Box();
    readonly TableLayoutPanel deviceList = Column();
    readonly Dictionary<string, TextBox> deviceUsers = new();
    readonly Label rdpStatus = Note("");
    string targetDevice;
    readonly EdgeMap edgeMap;
    readonly TrackBar strength = new() { Minimum = 200, Maximum = 2000, TickFrequency = 200, SmallChange = 50, LargeChange = 200, AutoSize = false, Height = 34 };
    readonly TrackBar hold = new() { Minimum = 0, Maximum = 800, TickFrequency = 100, SmallChange = 25, LargeChange = 100, AutoSize = false, Height = 34 };
    readonly Label strengthValue = Value(), holdValue = Value();
    readonly CheckBox stayConnected = Check("Stay connected while the portal is closed, so it reopens instantly");
    readonly CheckBox startup = Check("Start Async with Windows");
    readonly Button colorIn = Swatch(), colorOut = Swatch();

    readonly CheckBox sound = Check("Play sounds");
    readonly Dictionary<string, RadioButton> packRadios = new();
    readonly Dictionary<string, FolderPicker> folders = new();
    readonly Dictionary<(string Set, bool Opening), string> picks = new();
    readonly SoundPicker openPick = new(), closePick = new();
    string currentSet;

    readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Bg };
    readonly Dictionary<string, (NavButton Button, Control Page)> pages = new();

    /// <summary>Raised after Save with whether the connection details changed.</summary>
    public event Action<bool>? Saved;

    public SettingsForm(Settings cfg, CoreClient core, Icon icon, bool startsWithWindows)
    {
        this.cfg = cfg;
        this.core = core;
        this.icon = icon;
        targetDevice = cfg.TargetDevice;
        // Never set up: start from what Async did before edges were configurable (every free top edge).
        edgeMap = new EdgeMap(cfg.EdgeDevices != null
            ? new Dictionary<string, string>(cfg.EdgeDevices)
            : Screen.AllScreens.Where(s => cfg.IsConfigured && EdgeWatcher.IsFree(s.Bounds, Edge.Top))
                .ToDictionary(s => Settings.EdgeKey(s.DeviceName, Edge.Top), _ => cfg.TargetDevice))
        { BackColor = Card, Font = new Font("Segoe UI", 9f), Dock = DockStyle.Top, Height = 260 };

        Text = "Async settings";
        Icon = icon;
        BackColor = Bg;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(920, 720);

        hotkey.Text = cfg.Hotkey;
        strength.Value = Math.Clamp(cfg.PushDistance, strength.Minimum, strength.Maximum);
        hold.Value = Math.Clamp(cfg.PushHoldMs, hold.Minimum, hold.Maximum);
        stayConnected.Checked = cfg.StayConnected;
        startup.Checked = startsWithWindows;
        colorIn.BackColor = cfg.InColor;
        colorOut.BackColor = cfg.OutColor;
        colorIn.Click += (_, _) => PickColor(colorIn);
        colorOut.Click += (_, _) => PickColor(colorOut);
        strength.ValueChanged += (_, _) => strengthValue.Text = StrengthText();
        hold.ValueChanged += (_, _) => holdValue.Text = HoldText();
        strengthValue.Text = StrengthText();
        holdValue.Text = HoldText();

        sound.Checked = cfg.Sound;
        currentSet = Packs.Contains(cfg.SoundSet) ? cfg.SoundSet : SoundLibrary.BuiltIn;
        foreach (var set in Packs)
            foreach (var opening in new[] { true, false })
                picks[(set, opening)] = cfg.SoundFor(set, opening);
        folders[SoundLibrary.Portal] = new FolderPicker(cfg.PortalFolder);
        folders[SoundLibrary.Portal2] = new FolderPicker(cfg.Portal2Folder);
        foreach (var (game, picker) in folders) picker.Browse.Click += (_, _) => PickGameFolder(game);
        WirePicker(openPick, opening: true);
        WirePicker(closePick, opening: false);

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Side, Padding = new Padding(0, 18, 0, 12) };
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Side };
        var brand = new Label
        {
            Text = "Async", Dock = DockStyle.Top, Height = 54, ForeColor = Text1, Padding = new Padding(22, 0, 0, 14),
            Font = new Font("Segoe UI Semibold", 17f), TextAlign = ContentAlignment.MiddleLeft,
        };
        var version = new Label
        {
            Text = $"Version {Application.ProductVersion.Split('+')[0]}", Dock = DockStyle.Bottom, Height = 24, ForeColor = Dim,
            Font = new Font("Segoe UI", 8.5f), Padding = new Padding(22, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
        };
        sidebar.Controls.Add(nav);
        sidebar.Controls.Add(version);
        sidebar.Controls.Add(brand);

        AddPage(nav, "Devices", "Computers this PC can punch into, and how to sign in to each.", DevicesPage());
        AddPage(nav, "Screen edges", "Which computer each edge of your screens leads to.", EdgesPage());
        AddPage(nav, "Push to punch", "How deliberate a push has to be before the portal opens.", PushPage());
        AddPage(nav, "Look and feel", "Portal colours and how Async runs.", LookPage());
        AddPage(nav, "Sounds", "What opening and closing a portal sounds like.", SoundsPage());

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Bg, Padding = new Padding(24, 14, 24, 14) };
        footer.Paint += (_, e) => { using var p = new Pen(Line); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        var save = Action("Save", primary: true);
        save.Click += (_, _) => Save();
        var cancel = Action("Cancel");
        cancel.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = Bg };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        footer.Controls.Add(buttons);

        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(sidebar);
        AcceptButton = save;
        CancelButton = cancel;

        UpdateRdpStatus();
        FillDevices();
        core.DevicesChanged += FillDevices;
        RefreshPacks();
        ShowSet(currentSet);
        ShowPage("Devices");
    }

    public bool StartWithWindows => startup.Checked;

    // ---------- pages ----------

    void AddPage(FlowLayoutPanel nav, string title, string subtitle, Control body)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Bg, Padding = new Padding(32, 26, 32, 20), Visible = false };
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Bg };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 18f), ForeColor = Text1, Margin = new Padding(0, 0, 0, 2) });
        stack.Controls.Add(new Label { Text = subtitle, AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 0, 0, 18) });
        body.Dock = DockStyle.Fill;
        stack.Controls.Add(body);
        page.Controls.Add(stack);
        content.Controls.Add(page);

        var button = new NavButton(title);
        button.Click += (_, _) => ShowPage(title);
        nav.Controls.Add(button);
        pages[title] = (button, page);
    }

    public void ShowPage(string title)
    {
        foreach (var (name, (button, page)) in pages)
        {
            page.Visible = name == title;
            button.Selected = name == title;
        }
    }

    Control DevicesPage()
    {
        var body = Column();
        var thisPc = CardPanel();
        var rdpSettings = Action("Remote Desktop settings…");
        rdpSettings.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:remotedesktop") { UseShellExecute = true });
        var pcRow = Grid(2);
        pcRow.ColumnStyles[1] = new ColumnStyle(SizeType.AutoSize);
        var pcText = Column();
        pcText.Controls.Add(new Label { Text = $"This PC · {core.DeviceName}", AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Text1, Margin = new Padding(0, 0, 0, 4) });
        pcText.Controls.Add(rdpStatus);
        pcRow.Controls.Add(pcText, 0, 0);
        pcRow.Controls.Add(rdpSettings, 1, 0);
        rdpSettings.Anchor = AnchorStyles.Right;
        thisPc.Controls.Add(pcRow);
        body.Controls.Add(thisPc);

        body.Controls.Add(SectionLabel("Paired devices"));
        body.Controls.Add(deviceList);
        body.Controls.Add(Note("The selected device is the one the hotkey and the tray icon open. Mac and Linux computers only need their screen sharing password."));
        var pair = Action("Pair a device…", primary: true);
        pair.Margin = new Padding(0, 14, 0, 0);
        pair.Click += (_, _) => PairDialog.ShowFor(core, icon);
        body.Controls.Add(pair);
        return body;
    }

    Control EdgesPage()
    {
        var body = Column();
        var card = CardPanel();
        card.Padding = new Padding(8);
        card.Controls.Add(edgeMap);
        body.Controls.Add(card);
        body.Controls.Add(Note("Click the top or a side of a screen and pick the computer pushing through it opens. The bottom is always the way back. Edges where two of your screens meet aren't shown: the mouse just crosses over there."));
        return body;
    }

    Control PushPage()
    {
        var body = Column();
        body.Controls.Add(SliderCard("How hard", "How far you have to keep pushing the mouse into the edge.", strength, strengthValue));
        body.Controls.Add(SliderCard("How long", "The push has to last at least this long, so flicking past the edge doesn't count.", hold, holdValue));
        var closing = CardPanel();
        var c = Column();
        c.Controls.Add(new Label { Text = "Coming back", AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Text1, Margin = new Padding(0, 0, 0, 4) });
        c.Controls.Add(Note("Push down out of the bottom of the screen to close the portal, whichever edge you came in through."));
        closing.Controls.Add(c);
        body.Controls.Add(closing);
        var keyCard = CardPanel();
        var k = Grid(2);
        k.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 100);
        k.ColumnStyles[1] = new ColumnStyle(SizeType.Absolute, 200);
        var kText = Column();
        kText.Controls.Add(new Label { Text = "Hotkey", AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Text1 });
        kText.Controls.Add(Note("Opens or closes the portal from the keyboard. Takes effect after a restart."));
        hotkey.Dock = DockStyle.Fill;
        hotkey.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        k.Controls.Add(kText, 0, 0);
        k.Controls.Add(hotkey, 1, 0);
        keyCard.Controls.Add(k);
        body.Controls.Add(keyCard);
        return body;
    }

    Control LookPage()
    {
        var body = Column();
        var colors = CardPanel();
        var g = Grid(2);
        g.Controls.Add(SwatchBlock("Opening portal", colorIn), 0, 0);
        g.Controls.Add(SwatchBlock("Closing portal", colorOut), 1, 0);
        colors.Controls.Add(g);
        body.Controls.Add(colors);
        var run = CardPanel();
        var r = Column();
        r.Controls.Add(stayConnected);
        r.Controls.Add(startup);
        run.Controls.Add(r);
        body.Controls.Add(run);
        return body;
    }

    Control SoundsPage()
    {
        var body = Column();
        var top = CardPanel();
        var t = Column();
        t.Controls.Add(sound);
        t.Controls.Add(PackChooser());
        top.Controls.Add(t);
        body.Controls.Add(top);

        var games = CardPanel();
        var g = Column();
        g.Controls.Add(FieldLabel("Portal folder"));
        g.Controls.Add(folders[SoundLibrary.Portal]);
        g.Controls.Add(FieldLabel("Portal 2 folder"));
        g.Controls.Add(folders[SoundLibrary.Portal2]);
        g.Controls.Add(Note("Your own install, e.g. …\\steamapps\\common\\Portal 2. Sounds are read from it, never copied."));
        games.Controls.Add(g);
        body.Controls.Add(games);

        var picks = CardPanel();
        var p = Column();
        p.Controls.Add(FieldLabel("Opening"));
        p.Controls.Add(openPick);
        p.Controls.Add(FieldLabel("Closing"));
        p.Controls.Add(closePick);
        picks.Controls.Add(p);
        body.Controls.Add(picks);
        return body;
    }

    Control SliderCard(string title, string help, TrackBar bar, Label value)
    {
        var card = CardPanel();
        var g = Grid(2);
        g.ColumnStyles[1] = new ColumnStyle(SizeType.AutoSize);
        g.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Text1 }, 0, 0);
        value.Anchor = AnchorStyles.Right;
        g.Controls.Add(value, 1, 0);
        g.Controls.Add(Note(help), 0, 1);
        g.SetColumnSpan(g.GetControlFromPosition(0, 1)!, 2);
        bar.Dock = DockStyle.Fill;
        bar.BackColor = Card;
        g.Controls.Add(bar, 0, 2);
        g.SetColumnSpan(bar, 2);
        card.Controls.Add(g);
        return card;
    }

    static Control SwatchBlock(string label, Button swatch)
    {
        var c = Column();
        c.Controls.Add(FieldLabel(label));
        swatch.Dock = DockStyle.Fill;
        swatch.Margin = new Padding(0, 0, 12, 0);
        c.Controls.Add(swatch);
        return c;
    }

    string StrengthText() => strength.Value switch
    {
        < 500 => $"{strength.Value} · light nudge",
        < 1000 => $"{strength.Value} · firm push",
        _ => $"{strength.Value} · really shove it",
    };

    string HoldText() => $"{hold.Value} ms";

    void Save()
    {
        bool connectionChanged = deviceUsers.Any(kv => kv.Value.Text.Trim() != cfg.UserFor(kv.Key)) || targetDevice != cfg.TargetDevice;
        cfg.TargetDevice = targetDevice;
        cfg.EdgeDevices = new Dictionary<string, string>(edgeMap.Mapping);
        foreach (var (id, box) in deviceUsers) cfg.DeviceUsers[id] = box.Text.Trim();
        cfg.Hotkey = hotkey.Text.Trim();
        cfg.PushDistance = strength.Value;
        cfg.PushHoldMs = hold.Value;
        cfg.StayConnected = stayConnected.Checked;
        cfg.ColorIn = ColorTranslator.ToHtml(colorIn.BackColor);
        cfg.ColorOut = ColorTranslator.ToHtml(colorOut.BackColor);
        cfg.Sound = sound.Checked;
        cfg.SoundSet = currentSet;
        cfg.PortalFolder = folders[SoundLibrary.Portal].Path;
        cfg.Portal2Folder = folders[SoundLibrary.Portal2].Path;
        foreach (var ((set, opening), value) in picks) cfg.SetSound(set, opening, value);
        cfg.Save();
        Saved?.Invoke(connectionChanged);
        Close();
    }

    // ---------- devices ----------

    void FillDevices()
    {
        if (IsDisposed) return;
        deviceList.SuspendLayout();
        foreach (Control c in deviceList.Controls.Cast<Control>().ToList()) { deviceList.Controls.Remove(c); if (c is not TextBox) c.Dispose(); }
        if (core.Devices.All(d => d.Id != targetDevice)) targetDevice = core.Devices.FirstOrDefault()?.Id ?? "";
        edgeMap.SetDevices(core.Devices);
        if (core.Devices.Count == 0)
            deviceList.Controls.Add(Note("Nothing paired yet. Install Async on the other computer, then pair them."));
        foreach (var d in core.Devices) deviceList.Controls.Add(DeviceCard(d));
        deviceList.ResumeLayout();
    }

    /// <summary>For the README pictures: cards for made-up devices.</summary>
    internal void ShowSampleDevices(IReadOnlyList<Device> sample)
    {
        deviceList.Controls.Clear();
        foreach (var d in sample) deviceList.Controls.Add(DeviceCard(d));
    }

    Control DeviceCard(Device d)
    {
        var card = CardPanel();
        var g = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, BackColor = Card };
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var pick = new RadioButton { Text = d.Name, AutoSize = true, ForeColor = Text1, Font = new Font("Segoe UI Semibold", 10.5f), Checked = d.Id == targetDevice, Margin = new Padding(0, 4, 8, 0) };
        pick.CheckedChanged += (_, _) =>
        {
            if (!pick.Checked) return;
            targetDevice = d.Id;
            // Radio buttons in separate cards don't uncheck each other on their own.
            foreach (var other in deviceList.Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<TableLayoutPanel>()).SelectMany(t => t.Controls.OfType<RadioButton>()))
                if (other != pick) other.Checked = false;
        };
        var detail = new Label { Text = "Checking…", AutoSize = true, ForeColor = Dim, Font = new Font("Segoe UI", 9f), Margin = new Padding(20, 0, 8, 0) };
        var names = Column();
        names.Controls.Add(pick);
        names.Controls.Add(detail);

        if (!deviceUsers.TryGetValue(d.Id, out var user))
            deviceUsers[d.Id] = user = new TextBox { Text = cfg.UserFor(d.Id), PlaceholderText = "Windows user", BackColor = Field, ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle };
        user.Dock = DockStyle.Fill;
        user.Margin = new Padding(0, 6, 8, 0);
        var pass = Action("Password…");
        pass.Margin = new Padding(0, 2, 8, 0);
        pass.Click += (_, _) => Creds.Prompt(Settings.CredentialTarget(d.Id), user.Text.Trim(), d.Name);
        var remove = Action("Remove", danger: true);
        remove.Margin = new Padding(0, 2, 0, 0);
        remove.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, $"Unpair {d.Name}? It won't be able to punch into this PC, and you won't be able to punch into it, until you pair again.", "Async", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { await core.Remove(d.Id); } catch (Exception e) { MessageBox.Show(this, e.Message, "Async"); }
        };
        g.Controls.Add(names, 0, 0);
        g.Controls.Add(user, 1, 0);
        g.Controls.Add(pass, 2, 0);
        g.Controls.Add(remove, 3, 0);
        card.Controls.Add(g);
        _ = Describe(d, detail, user);
        return card;
    }

    /// <summary>Asks the device what it is, so its card can say e.g. "macOS · screen sharing on".</summary>
    async Task Describe(Device d, Label detail, TextBox user)
    {
        try
        {
            var (os, services, vncPassword) = await core.Describe(d.Id);
            if (IsDisposed) return;
            string osName = os switch { "windows" => "Windows", "macos" => "macOS", "linux" => "Linux", _ => os };
            detail.Text = services.Length == 0 ? $"{osName} · screen sharing is off"
                : vncPassword != null && !services.Contains("rdp") ? $"{osName} · ready, nothing to enter"
                : $"{osName} · ready";
            detail.ForeColor = services.Length == 0 ? Color.FromArgb(255, 170, 90) : Color.FromArgb(120, 220, 170);
            if (!services.Contains("rdp") && services.Contains("vnc")) { user.PlaceholderText = vncPassword != null ? "nothing needed" : "no user needed"; }
        }
        catch { if (!IsDisposed) { detail.Text = "Offline: is Async running on it?"; detail.ForeColor = Dim; } }
    }

    void UpdateRdpStatus()
    {
        bool on = false;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Control\Terminal Server");
            on = key?.GetValue("fDenyTSConnections") is int deny && deny == 0;
        }
        catch { }
        rdpStatus.Text = on
            ? "Remote Desktop is on, so paired devices can punch into this PC."
            : "Remote Desktop is off, so other devices can't punch into this PC (needs Windows Pro). You can still punch out of it.";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        core.DevicesChanged -= FillDevices;
        base.OnFormClosed(e);
    }

    // ---------- sounds ----------

    Control PackChooser()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0), BackColor = Card };
        foreach (var set in Packs)
        {
            var radio = new RadioButton { AutoSize = true, ForeColor = Text1, Checked = set == currentSet, Margin = new Padding(0, 4, 18, 4) };
            radio.CheckedChanged += (_, _) => { if (radio.Checked) ShowSet(set); };
            packRadios[set] = radio;
            row.Controls.Add(radio);
        }
        return row;
    }

    /// <summary>Game packs are only selectable once their folder points at a real install.</summary>
    void RefreshPacks()
    {
        foreach (var (set, radio) in packRadios)
        {
            bool game = folders.TryGetValue(set, out var folder);
            bool ready = !game || SoundLibrary.FindArchive(set, folder!.Path) != null;
            radio.Text = set == SoundLibrary.Custom ? "Custom files" : ready ? set : $"{set} (set folder)";
            radio.Enabled = ready;
        }
        if (!packRadios[currentSet].Enabled) packRadios[SoundLibrary.BuiltIn].Checked = true;
    }

    void PickGameFolder(string game)
    {
        using var dlg = new FolderBrowserDialog { Description = $"Pick your {game} install folder (…\\steamapps\\common\\{game})", UseDescriptionForTitle = true };
        if (!string.IsNullOrEmpty(folders[game].Path)) dlg.InitialDirectory = folders[game].Path;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (SoundLibrary.FindArchive(game, dlg.SelectedPath) == null)
        {
            MessageBox.Show(this, $"That folder doesn't look like a {game} install.\n\nPick the folder Steam installed it to, for example\n…\\steamapps\\common\\{game}",
                "Async", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        folders[game].Path = dlg.SelectedPath;
        RefreshPacks();
        packRadios[game].Checked = true;
    }

    void ShowSet(string set)
    {
        currentSet = set;
        Fill(openPick, opening: true);
        Fill(closePick, opening: false);
    }

    void Fill(SoundPicker p, bool opening)
    {
        string choice = picks[(currentSet, opening)];
        p.Filling = true;
        p.Combo.Items.Clear();
        p.Browse.Visible = currentSet == SoundLibrary.Custom;
        switch (currentSet)
        {
            case SoundLibrary.Portal or SoundLibrary.Portal2:
                foreach (var key in SoundLibrary.GameSounds) p.Combo.Items.Add(new SoundItem(key, SoundLibrary.Label(key)));
                p.Combo.SelectedItem = p.Combo.Items.Cast<SoundItem>().FirstOrDefault(i => i.Value == choice) ?? p.Combo.Items[0];
                picks[(currentSet, opening)] = ((SoundItem)p.Combo.SelectedItem!).Value;
                break;
            case SoundLibrary.Custom:
                p.Combo.Items.Add(new SoundItem(choice, string.IsNullOrEmpty(choice) ? "Choose a .wav file →" : Path.GetFileName(choice)));
                p.Combo.SelectedIndex = 0;
                break;
            default:
                p.Combo.Items.Add(new SoundItem("", opening ? "Whoosh up (built-in)" : "Whoosh down (built-in)"));
                p.Combo.SelectedIndex = 0;
                break;
        }
        p.Combo.Enabled = true; // a disabled combo box ignores the dark colours
        p.Filling = false;
    }

    void WirePicker(SoundPicker p, bool opening)
    {
        p.Combo.SelectedIndexChanged += (_, _) =>
        {
            if (!p.Filling && p.Combo.SelectedItem is SoundItem item && currentSet is SoundLibrary.Portal or SoundLibrary.Portal2)
                picks[(currentSet, opening)] = item.Value;
        };
        p.Browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "WAV sounds (*.wav)|*.wav", Title = opening ? "Opening sound" : "Closing sound" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            picks[(SoundLibrary.Custom, opening)] = dlg.FileName;
            Fill(p, opening);
        };
        p.Play.Click += (_, _) =>
        {
            string? folder = folders.TryGetValue(currentSet, out var f) ? f.Path : null;
            SoundLibrary.Player(currentSet, picks[(currentSet, opening)], folder, opening).Play();
        };
    }

    sealed record SoundItem(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    sealed class SoundPicker : TableLayoutPanel
    {
        public readonly ComboBox Combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Field, ForeColor = Text1, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 0) };
        public readonly Button Browse = Action("Browse…"), Play = Action("▶ Play");
        public bool Filling;

        public SoundPicker()
        {
            ColumnCount = 3;
            AutoSize = true;
            Dock = DockStyle.Top;
            BackColor = Card;
            Margin = new Padding(0, 0, 0, 6);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Browse.Margin = new Padding(0, 0, 8, 0);
            Play.Margin = Padding.Empty;
            Controls.Add(Combo, 0, 0);
            Controls.Add(Browse, 1, 0);
            Controls.Add(Play, 2, 0);
        }
    }

    sealed class FolderPicker : TableLayoutPanel
    {
        readonly TextBox box = new() { ReadOnly = true, BackColor = Field, ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 0) };
        public readonly Button Browse = Action("Browse…");

        public FolderPicker(string path)
        {
            ColumnCount = 2;
            AutoSize = true;
            Dock = DockStyle.Top;
            BackColor = Card;
            Margin = new Padding(0, 0, 0, 6);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Browse.Margin = Padding.Empty;
            Controls.Add(box, 0, 0);
            Controls.Add(Browse, 1, 0);
            Path = path;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Path
        {
            get => box.Text;
            set { box.Text = value; box.PlaceholderText = "Not set"; }
        }
    }

    void PickColor(Button swatch)
    {
        using var dlg = new ColorDialog { Color = swatch.BackColor, FullOpen = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) swatch.BackColor = dlg.Color;
    }

    // ---------- little UI helpers ----------

    /// <summary>A sidebar entry: highlighted with an accent bar when its page is showing.</summary>
    sealed class NavButton : Control
    {
        bool selected, hot;

        public NavButton(string text)
        {
            Text = text;
            Size = new Size(200, 42);
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 10.5f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected
        {
            get => selected;
            set { selected = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(selected ? Color.FromArgb(34, 29, 52) : hot ? Color.FromArgb(24, 21, 36) : Side);
            if (selected) using (var bar = new SolidBrush(Accent)) g.FillRectangle(bar, 0, 8, 3, Height - 16);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(22, 0, Width - 22, Height), selected ? Text1 : Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    static Panel CardPanel()
    {
        var p = new Panel { BackColor = Card, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 14, 16, 14), Margin = new Padding(0, 0, 0, 12) };
        p.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Line);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        return p;
    }

    static TableLayoutPanel Column()
    {
        var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    static TableLayoutPanel Grid(int columns)
    {
        var t = new TableLayoutPanel { ColumnCount = columns, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty, BackColor = Color.Transparent };
        for (int i = 0; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        return t;
    }

    static Label SectionLabel(string text) => new() { Text = text.ToUpperInvariant(), AutoSize = true, ForeColor = Dim, Font = new Font("Segoe UI Semibold", 8.5f), Margin = new Padding(0, 10, 0, 8) };
    static Label FieldLabel(string text) => new() { Text = text, AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 4, 0, 4) };
    static Label Value() => new() { AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 10f) };
    static TextBox Box() => new() { BackColor = Field, ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle };
    static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Dim, Font = new Font("Segoe UI", 9f), Margin = new Padding(0, 4, 0, 4) };
    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Text1, Margin = new Padding(0, 4, 0, 4) };
    static Button Swatch() => new() { Text = "", Height = 36, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };

    static Button Action(string text, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 34), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? AccentDark : Field, ForeColor = danger ? Color.FromArgb(255, 140, 150) : Text1,
            Padding = new Padding(12, 0, 12, 0), Cursor = Cursors.Hand, Margin = new Padding(8, 0, 0, 0),
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Line;
        b.FlatAppearance.MouseOverBackColor = primary ? Accent : Color.FromArgb(52, 46, 74);
        return b;
    }
}
