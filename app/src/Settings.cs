using System.Text.Json;

namespace Async;

sealed class Settings
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Async");
    public static readonly string FilePath = Path.Combine(Folder, "settings.json");

    /// <summary>The paired device the hotkey and tray icon open (its device id).</summary>
    public string TargetDevice { get; set; } = "";
    /// <summary>
    /// Which paired device each monitor edge punches into, keyed by <see cref="EdgeKey"/>. Null until
    /// set in Settings, which means: every free top edge opens <see cref="TargetDevice"/>.
    /// </summary>
    public Dictionary<string, string>? EdgeDevices { get; set; }
    /// <summary>
    /// What each mapped edge does, keyed like <see cref="EdgeDevices"/>: "portal" (the other
    /// computer's screen opens here) or "mouse" (the pointer and keyboard move over to it, as if it
    /// were another monitor). Missing means portal.
    /// </summary>
    public Dictionary<string, string> EdgeModes { get; set; } = new();
    /// <summary>How hard to push through a mouse-mode edge; lighter than a portal, like a monitor with a little resistance.</summary>
    public int MousePushDistance { get; set; } = 120;
    /// <summary>Where files from another computer went last time (the folder picker starts there).</summary>
    public string LastDropFolder { get; set; } = "";
    /// <summary>The Windows account to sign in as on each paired device, by device id.</summary>
    public Dictionary<string, string> DeviceUsers { get; set; } = new();

    /// <summary>How hard to push against the edge, in raw mouse counts (about 1 cm of travel on a 1600 DPI mouse per 600).</summary>
    public int PushDistance { get; set; } = 700;
    /// <summary>A pause longer than this resets the push.</summary>
    public int PushGapMs { get; set; } = 300;
    /// <summary>The push has to last at least this long, so a fast flick that overshoots the edge doesn't count.</summary>
    public int PushHoldMs { get; set; } = 250;
    public string Hotkey { get; set; } = "Ctrl+Alt+Up";

    /// <summary>Keep the session alive while the portal is closed so the next punch is instant.</summary>
    public bool StayConnected { get; set; } = true;
    public string ColorIn { get; set; } = "#B05CFF";
    public string ColorOut { get; set; } = "#FF9A2E";
    public int OpenMs { get; set; } = 700;
    public int CloseMs { get; set; } = 480;

    public bool Sound { get; set; } = true;
    /// <summary>Built-in, Portal, Portal 2 or Custom. Each pack remembers its own picks.</summary>
    public string SoundSet { get; set; } = SoundLibrary.BuiltIn;
    /// <summary>Install folders of the user's own copies of the games, picked in Settings.</summary>
    public string PortalFolder { get; set; } = "";
    public string Portal2Folder { get; set; } = "";
    public string PortalOpen { get; set; } = "entry";
    public string PortalClose { get; set; } = "exit";
    public string Portal2Open { get; set; } = "entry";
    public string Portal2Close { get; set; } = "exit";
    public string CustomOpen { get; set; } = "";
    public string CustomClose { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(TargetDevice);

    public string UserFor(string device) => DeviceUsers.TryGetValue(device, out var u) ? u : "";

    public static string EdgeKey(string screen, Edge e) => $"{screen}/{e}";

    /// <summary>The device pushing through this edge opens, or null if the edge does nothing.</summary>
    public string? DeviceFor(string screen, Edge e)
    {
        // The bottom is always the way back.
        if (e == Edge.Bottom) return null;
        if (EdgeDevices == null) return e == Edge.Top && IsConfigured ? TargetDevice : null;
        return EdgeDevices.TryGetValue(EdgeKey(screen, e), out var d) && d != "" ? d : null;
    }

    public bool IsMouseEdge(string screen, Edge e) =>
        e != Edge.Bottom && EdgeModes.TryGetValue(EdgeKey(screen, e), out var m) && m == "mouse";

    /// <summary>Windows Credential Manager entry holding a device's password.</summary>
    public static string CredentialTarget(string device) => $"Async:{device}";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        Directory.CreateDirectory(Folder);
        Settings s = new();
        try
        {
            if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) { Log.Write($"settings.json unreadable, using defaults: {e.Message}"); }
        s.Save(); // write back so new options show up in the file
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch (Exception e) { Log.Write($"Couldn't save settings: {e.Message}"); }
    }

    public string SoundFor(string set, bool opening) => set switch
    {
        SoundLibrary.Portal => opening ? PortalOpen : PortalClose,
        SoundLibrary.Portal2 => opening ? Portal2Open : Portal2Close,
        SoundLibrary.Custom => opening ? CustomOpen : CustomClose,
        _ => "",
    };

    public void SetSound(string set, bool opening, string value)
    {
        switch (set)
        {
            case SoundLibrary.Portal: if (opening) PortalOpen = value; else PortalClose = value; break;
            case SoundLibrary.Portal2: if (opening) Portal2Open = value; else Portal2Close = value; break;
            case SoundLibrary.Custom: if (opening) CustomOpen = value; else CustomClose = value; break;
        }
    }

    public string? GameFolder(string set) => set switch
    {
        SoundLibrary.Portal => PortalFolder,
        SoundLibrary.Portal2 => Portal2Folder,
        _ => null,
    };

    public Color InColor => ParseColor(ColorIn, Color.FromArgb(176, 92, 255));
    public Color OutColor => ParseColor(ColorOut, Color.FromArgb(255, 154, 46));

    static Color ParseColor(string hex, Color fallback)
    {
        try { return ColorTranslator.FromHtml(hex); } catch { return fallback; }
    }

    /// <summary>"Ctrl+Alt+Up" -> (modifiers, virtual key). Returns false if it doesn't parse.</summary>
    public bool TryParseHotkey(out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(Hotkey)) return false;
        foreach (var part in Hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default:
                    if (!Enum.TryParse(part, true, out Keys key)) return false;
                    vk = (uint)key;
                    break;
            }
        }
        mods |= 0x4000; // MOD_NOREPEAT
        return vk != 0;
    }
}
