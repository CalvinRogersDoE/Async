namespace Async;

/// <summary>The settings window (tray → Settings…). Changes apply when you press Save.</summary>
sealed class SettingsForm : Form
{
    static readonly Color Bg = Color.FromArgb(18, 16, 28), Field = Color.FromArgb(32, 28, 48), Text1 = Color.FromArgb(235, 230, 250), Dim = Color.FromArgb(160, 150, 190);
    static readonly string[] Packs = [SoundLibrary.BuiltIn, SoundLibrary.Portal, SoundLibrary.Portal2, SoundLibrary.Custom];

    readonly Settings cfg;
    readonly CoreClient core;
    readonly TextBox hotkey = Box();
    readonly TableLayoutPanel deviceList = new() { ColumnCount = 4, AutoSize = true, Margin = Padding.Empty };
    readonly Dictionary<string, TextBox> deviceUsers = new();
    readonly Label rdpStatus = Note("");
    string targetDevice;
    readonly TrackBar strength = new() { Minimum = 200, Maximum = 2000, TickFrequency = 200, SmallChange = 50, LargeChange = 200, AutoSize = false, Height = 32 };
    readonly TrackBar hold = new() { Minimum = 0, Maximum = 800, TickFrequency = 100, SmallChange = 25, LargeChange = 100, AutoSize = false, Height = 32 };
    readonly Label strengthValue = Note(""), holdValue = Note("");
    readonly CheckBox topToggles = Check("Pushing through the top again closes the portal");
    readonly CheckBox bottomCloses = Check("Pushing out the bottom closes the portal");
    readonly CheckBox stayConnected = Check("Stay connected while the portal is closed (instant reopen)");
    readonly CheckBox startup = Check("Start Async with Windows");
    readonly Button colorIn = Swatch(), colorOut = Swatch();

    readonly CheckBox sound = Check("Play sounds");
    readonly Dictionary<string, RadioButton> packRadios = new();
    readonly Dictionary<string, FolderPicker> folders = new();
    readonly Dictionary<(string Set, bool Opening), string> picks = new();
    readonly SoundPicker openPick = new(), closePick = new();
    string currentSet;

    /// <summary>Raised after Save with whether the connection details changed.</summary>
    public event Action<bool>? Saved;

    public SettingsForm(Settings cfg, CoreClient core, Icon icon, bool startsWithWindows)
    {
        this.cfg = cfg;
        this.core = core;
        targetDevice = cfg.TargetDevice;
        Text = "Async settings";
        Icon = icon;
        BackColor = Bg;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);

        hotkey.Text = cfg.Hotkey;
        strength.Value = Math.Clamp(cfg.PushDistance, strength.Minimum, strength.Maximum);
        hold.Value = Math.Clamp(cfg.PushHoldMs, hold.Minimum, hold.Maximum);
        topToggles.Checked = cfg.TopPushToggles;
        bottomCloses.Checked = cfg.BottomPushCloses;
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

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));

        Heading(grid, "Devices");
        Row(grid, "This PC", Note($"{core.DeviceName}. Async must be running on any PC you want to punch into."));
        Row(grid, "Punch into", deviceList);
        var pair = Action("Pair a device…");
        pair.Click += (_, _) => PairDialog.ShowFor(core, icon);
        Row(grid, "", pair);
        var rdpSettings = Action("Remote Desktop settings…");
        rdpSettings.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:remotedesktop") { UseShellExecute = true });
        Row(grid, "Others into this PC", Stack(rdpStatus, rdpSettings));
        UpdateRdpStatus();
        FillDevices();
        core.DevicesChanged += FillDevices;

        Heading(grid, "Push to punch");
        Row(grid, "How hard", Stack(strength, strengthValue));
        Row(grid, "How long", Stack(hold, holdValue));
        Row(grid, "", topToggles);
        Row(grid, "", bottomCloses);
        Row(grid, "Hotkey", Stack(hotkey, Note("e.g. Ctrl+Alt+Up (takes effect after a restart)")));

        Heading(grid, "Look and feel");
        Row(grid, "Opening portal", colorIn);
        Row(grid, "Closing portal", colorOut);
        Row(grid, "", stayConnected);
        Row(grid, "", startup);

        Heading(grid, "Sounds");
        Row(grid, "", sound);
        Row(grid, "Sound pack", PackChooser());
        Row(grid, "Portal folder", folders[SoundLibrary.Portal]);
        Row(grid, "Portal 2 folder", Stack(folders[SoundLibrary.Portal2], Note("Your own install, e.g. …\\steamapps\\common\\Portal 2. Sounds are read from it, never copied.")));
        Row(grid, "Opening", openPick);
        Row(grid, "Closing", closePick);
        RefreshPacks();
        ShowSet(currentSet);

        var save = Action("Save");
        save.BackColor = Color.FromArgb(120, 60, 200);
        save.Click += (_, _) => Save();
        var cancel = Action("Cancel");
        cancel.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 14, 0, 0) };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        grid.Controls.Add(buttons, 0, grid.RowCount);
        grid.SetColumnSpan(buttons, 2);

        Controls.Add(grid);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public bool StartWithWindows => startup.Checked;

    string StrengthText() => strength.Value switch
    {
        < 500 => $"{strength.Value} (light nudge)",
        < 1000 => $"{strength.Value} (firm push)",
        _ => $"{strength.Value} (really shove it)",
    };

    string HoldText() => $"{hold.Value} ms (a quick flick is about 100 ms)";

    void Save()
    {
        bool connectionChanged = targetDevice != cfg.TargetDevice || deviceUsers.TryGetValue(targetDevice, out var tu) && tu.Text.Trim() != cfg.UserFor(targetDevice);
        cfg.TargetDevice = targetDevice;
        foreach (var (id, box) in deviceUsers) cfg.DeviceUsers[id] = box.Text.Trim();
        cfg.Hotkey = hotkey.Text.Trim();
        cfg.PushDistance = strength.Value;
        cfg.PushHoldMs = hold.Value;
        cfg.TopPushToggles = topToggles.Checked;
        cfg.BottomPushCloses = bottomCloses.Checked;
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
        deviceList.Controls.Clear();
        deviceList.RowCount = 0;
        if (core.Devices.Count == 0)
        {
            deviceList.Controls.Add(Note("Nothing paired yet. Install Async on the other PC, then pair them."), 0, 0);
            deviceList.SetColumnSpan(deviceList.Controls[0], 4);
        }
        if (core.Devices.All(d => d.Id != targetDevice)) targetDevice = core.Devices.FirstOrDefault()?.Id ?? "";
        foreach (var d in core.Devices)
        {
            int row = deviceList.RowCount++;
            var pick = new RadioButton { Text = d.Name, AutoSize = true, ForeColor = Text1, Checked = d.Id == targetDevice, Margin = new Padding(0, 6, 10, 0) };
            pick.CheckedChanged += (_, _) => { if (pick.Checked) targetDevice = d.Id; };
            if (!deviceUsers.TryGetValue(d.Id, out var user))
                deviceUsers[d.Id] = user = new TextBox { Text = cfg.UserFor(d.Id), PlaceholderText = "Windows user", Width = 130, BackColor = Field, ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 6, 0) };
            var pass = SmallButton("Password…", 90);
            pass.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(user.Text)) { user.Focus(); return; }
                Creds.Prompt(Settings.CredentialTarget(d.Id), user.Text.Trim(), d.Name);
            };
            var remove = SmallButton("Remove", 70);
            remove.Click += async (_, _) =>
            {
                if (MessageBox.Show(this, $"Unpair {d.Name}? It won't be able to punch into this PC, and you won't be able to punch into it, until you pair again.", "Async", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                try { await core.Remove(d.Id); } catch (Exception e) { MessageBox.Show(this, e.Message, "Async"); }
            };
            deviceList.Controls.Add(pick, 0, row);
            deviceList.Controls.Add(user, 1, row);
            deviceList.Controls.Add(pass, 2, row);
            deviceList.Controls.Add(remove, 3, row);
        }
        deviceList.ResumeLayout();
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
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        foreach (var set in Packs)
        {
            var radio = new RadioButton { AutoSize = true, ForeColor = Text1, Checked = set == currentSet, Margin = new Padding(0, 4, 14, 4) };
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
        p.Combo.Enabled = currentSet is SoundLibrary.Portal or SoundLibrary.Portal2;
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
        public readonly ComboBox Combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Field, ForeColor = Text1, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill };
        public readonly Button Browse = SmallButton("Browse…", 90), Play = SmallButton("▶ Play", 70);
        public bool Filling;

        public SoundPicker()
        {
            ColumnCount = 3;
            AutoSize = true;
            Margin = Padding.Empty;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(Combo, 0, 0);
            Controls.Add(Browse, 1, 0);
            Controls.Add(Play, 2, 0);
        }
    }

    sealed class FolderPicker : TableLayoutPanel
    {
        readonly TextBox box = new() { ReadOnly = true, BackColor = Field, ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill };
        public readonly Button Browse = SmallButton("Browse…", 90);

        public FolderPicker(string path)
        {
            ColumnCount = 2;
            AutoSize = true;
            Margin = Padding.Empty;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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

    static void Heading(TableLayoutPanel grid, string text)
    {
        var l = new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), ForeColor = Color.FromArgb(200, 160, 255), Margin = new Padding(0, grid.RowCount == 0 ? 0 : 16, 0, 6) };
        grid.Controls.Add(l, 0, grid.RowCount);
        grid.SetColumnSpan(l, 2);
        grid.RowCount++;
    }

    static void Row(TableLayoutPanel grid, string label, Control c)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Dim, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 14, 6) }, 0, grid.RowCount);
        c.Dock = DockStyle.Fill;
        grid.Controls.Add(c, 1, grid.RowCount);
        grid.RowCount++;
    }

    static Control Stack(Control top, Control bottom)
    {
        var p = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = Padding.Empty };
        top.Dock = DockStyle.Fill;
        p.Controls.Add(top);
        p.Controls.Add(bottom);
        return p;
    }

    static TextBox Box() => new() { BackColor = Field, ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle };
    static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Dim, Font = new Font("Segoe UI", 8.5f) };
    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Text1 };
    static Button Swatch() => new() { Text = "", Height = 28, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };

    static Button Action(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1,
        Padding = new Padding(10, 2, 10, 2), Cursor = Cursors.Hand, Anchor = AnchorStyles.Left,
    };

    static Button SmallButton(string text, int width) => new()
    {
        Text = text, AutoSize = false, Size = new Size(width, 28), FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1,
        Cursor = Cursors.Hand, Margin = new Padding(6, 0, 0, 0),
    };
}
