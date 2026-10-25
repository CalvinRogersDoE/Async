namespace Async;

/// <summary>Pairs this PC with another one: show a code here, or type in the code the other PC shows.</summary>
sealed class PairDialog : Form
{
    static PairDialog? open;

    readonly CoreClient core;
    readonly Label heading = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), ForeColor = Theme.Accent };
    readonly Label body = new() { AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Theme.Dim };
    readonly Label code = new() { AutoSize = true, Font = new Font("Consolas", 34f, FontStyle.Bold), ForeColor = Theme.Text, Margin = new Padding(0, 14, 0, 6) };
    readonly TextBox entry = new() { Font = new Font("Consolas", 22f), CharacterCasing = CharacterCasing.Upper, MaxLength = 9, Width = 230, BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center };
    readonly FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 16, 0, 0) };
    readonly System.Windows.Forms.Timer countdown = new() { Interval = 1000 };
    DateTime expires;
    bool showingCode;

    public static void ShowFor(CoreClient core, Icon icon)
    {
        if (open is { IsDisposed: false }) { open.Activate(); return; }
        open = new PairDialog(core, icon);
        open.Show();
        Native.ForceForeground(open.Handle);
    }

    PairDialog(CoreClient core, Icon icon)
    {
        this.core = core;
        Text = "Pair a device";
        Icon = icon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(22);

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, MinimumSize = new Size(440, 0) };
        stack.Controls.AddRange([heading, body, code, entry, buttons]);
        Controls.Add(stack);

        countdown.Tick += (_, _) => Tick();
        core.Paired += OnPaired;
        core.PairEnded += OnPairEnded;
        entry.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = Join(); } };
        Choose();
    }

    void Choose()
    {
        Show("Pair a device",
            $"Install Async on both computers. On one of them, show a code. On the other, type it in.\n\nThis PC is {core.DeviceName}.",
            code: false, entryBox: false,
            ("Show a code", () => _ = ShowCode()), ("Enter a code", EnterCode), ("Cancel", Close));
    }

    async Task ShowCode()
    {
        Show("Your pairing code", "Type this into Async on the other computer (tray icon → Pair a device… → Enter a code).", code: true, entryBox: false, ("Cancel", Close));
        code.Text = "····-····";
        try
        {
            code.Text = await core.ShowPairingCode();
            showingCode = true;
            expires = DateTime.Now.AddMinutes(10);
            countdown.Start();
            Tick();
        }
        catch (Exception e) { Fail(e.Message); }
    }

    void EnterCode()
    {
        Show("Enter the code", "Type the code shown on the other computer.", code: false, entryBox: true, ("Pair", () => _ = Join()), ("Back", Choose));
        entry.Text = "";
        entry.Focus();
    }

    async Task Join()
    {
        var typed = entry.Text.Trim();
        if (typed.Replace("-", "").Length != 8) { body.Text = "Codes look like ABCD-EFGH."; return; }
        Show("Pairing…", "Looking for the other computer. This can take up to a minute the first time.", code: false, entryBox: false);
        try { Done(await core.PairWithCode(typed)); }
        catch (Exception e) { Fail(e.Message); }
    }

    void OnPaired(Device d)
    {
        if (showingCode) Done(d);
    }

    void OnPairEnded(string why)
    {
        if (showingCode) Fail(why);
    }

    void Done(Device d)
    {
        showingCode = false;
        countdown.Stop();
        Show($"Paired with {d.Name}", $"{d.Name} and {core.DeviceName} can now punch into each other.\n\nNext: in Settings, enter the Windows user and password Async should sign in with on {d.Name}.",
            code: false, entryBox: false, ("Done", Close));
    }

    void Fail(string why)
    {
        showingCode = false;
        countdown.Stop();
        Show("That didn't work", why, code: false, entryBox: false, ("Try again", Choose), ("Close", Close));
    }

    void Tick()
    {
        var left = expires - DateTime.Now;
        if (left <= TimeSpan.Zero) { countdown.Stop(); return; }
        body.Text = $"Type this into Async on the other computer (tray icon → Pair a device… → Enter a code).\n\nWaiting… the code works for another {left:m\\:ss}.";
    }

    void Show(string title, string text, bool code, bool entryBox, params (string Label, Action Click)[] actions)
    {
        heading.Text = title;
        body.Text = text;
        this.code.Visible = code;
        entry.Visible = entryBox;
        buttons.Controls.Clear();
        foreach (var (label, click) in actions)
        {
            var b = Theme.Button(label);
            b.Click += (_, _) => click();
            buttons.Controls.Add(b);
        }
        if (buttons.Controls.Count > 0) ((Button)buttons.Controls[0]).BackColor = Theme.Primary;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        countdown.Dispose();
        core.Paired -= OnPaired;
        core.PairEnded -= OnPairEnded;
        if (showingCode) _ = core.CancelPairing();
        base.OnFormClosed(e);
    }
}

/// <summary>Colours and controls shared by Async's windows.</summary>
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(18, 16, 28), Field = Color.FromArgb(32, 28, 48),
        Text = Color.FromArgb(235, 230, 250), Dim = Color.FromArgb(160, 150, 190),
        Accent = Color.FromArgb(200, 160, 255), Primary = Color.FromArgb(120, 60, 200);

    public static Button Button(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text,
        Padding = new Padding(10, 2, 10, 2), Margin = new Padding(0, 0, 8, 0), Cursor = Cursors.Hand,
    };
}
