using System.Media;

namespace Async;

/// <summary>
/// Sound packs: the built-in synthesised whoosh, the portal sounds from the user's own copy of
/// Portal or Portal 2 (read straight out of the game's .vpk archive, never copied anywhere), or
/// any .wav files the user picks.
/// </summary>
static class SoundLibrary
{
    public const string BuiltIn = "Built-in", Portal = "Portal", Portal2 = "Portal 2", Custom = "Custom";

    /// <summary>The four sounds each game offers, keyed by the name stored in settings.</summary>
    public static readonly string[] GameSounds = ["entry", "exit", "open", "close"];

    public static string Label(string key) => key switch
    {
        "entry" => "Portal entry",
        "exit" => "Portal exit",
        "open" => "Portal opening",
        "close" => "Portal closing",
        _ => key,
    };

    sealed record Game(string DirVpk, Dictionary<string, string> Sounds);

    static readonly Dictionary<string, Game> Games = new()
    {
        [Portal] = new(@"portal\portal_pak_dir.vpk", new()
        {
            ["entry"] = "sound/player/portal_enter1.wav",
            ["exit"] = "sound/player/portal_exit1.wav",
            ["open"] = "sound/weapons/portalgun/portal_open1.wav",
            ["close"] = "sound/weapons/portalgun/portal_close1.wav",
        }),
        [Portal2] = new(@"portal2\pak01_dir.vpk", new()
        {
            ["entry"] = "sound/weapons/portalgun/portal_enter_01.wav",
            ["exit"] = "sound/weapons/portalgun/portal_exit_01.wav",
            ["open"] = "sound/weapons/portalgun/portal_open_blue_01.wav",
            ["close"] = "sound/weapons/portalgun/portal_close1.wav",
        }),
    };

    static readonly Dictionary<string, Vpk?> Opened = new();

    /// <summary>
    /// The archive inside a game folder, or null if the folder isn't that game. Accepts the
    /// install folder (…\common\Portal) or the content folder inside it (…\Portal\portal).
    /// </summary>
    public static string? FindArchive(string game, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Games.TryGetValue(game, out var g)) return null;
        foreach (var dir in new[] { folder, Path.GetDirectoryName(folder) })
        {
            if (dir == null) continue;
            var file = Path.Combine(dir, g.DirVpk);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>A ready-to-play sound for one portal event, falling back to the built-in whoosh.</summary>
    public static SoundPlayer Player(Settings cfg, bool opening) =>
        Player(cfg.SoundSet, cfg.SoundFor(cfg.SoundSet, opening), cfg.GameFolder(cfg.SoundSet), opening);

    public static SoundPlayer Player(string set, string? choice, string? gameFolder, bool opening)
    {
        try
        {
            byte[]? wav = set switch
            {
                Portal or Portal2 => ReadGameSound(set, gameFolder, choice),
                Custom when File.Exists(choice) => File.ReadAllBytes(choice),
                _ => null,
            };
            if (wav != null)
            {
                var player = new SoundPlayer(new MemoryStream(wav));
                player.Load();
                return player;
            }
        }
        catch (Exception e) { Log.Write($"Sound {set}/{choice} failed, using the built-in one: {e.Message}"); }
        return Whoosh.Make(opening);
    }

    static byte[]? ReadGameSound(string game, string? folder, string? key)
    {
        var archive = FindArchive(game, folder);
        if (archive == null || key == null || !Games[game].Sounds.TryGetValue(key, out var path)) return null;
        if (!Opened.TryGetValue(archive, out var vpk))
        {
            try { vpk = new Vpk(archive); }
            catch (Exception e) { Log.Write($"Couldn't read {archive}: {e.Message}"); vpk = null; }
            Opened[archive] = vpk;
        }
        return vpk?.Read(path);
    }

    /// <summary>Minimal reader for Valve's VPK (v1/v2) archives: the directory tree plus file reads.</summary>
    sealed class Vpk
    {
        readonly record struct Entry(ushort Archive, uint Offset, uint Length, byte[] Preload);

        readonly string dirFile, prefix;
        readonly long dataStart;
        readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

        public Vpk(string dirFile)
        {
            this.dirFile = dirFile;
            prefix = dirFile[..^"dir.vpk".Length];
            using var r = new BinaryReader(File.OpenRead(dirFile));
            if (r.ReadUInt32() != 0x55AA1234) throw new InvalidDataException("not a VPK");
            uint version = r.ReadUInt32(), treeSize = r.ReadUInt32();
            if (version == 2) r.ReadBytes(16);
            dataStart = r.BaseStream.Position + treeSize;
            for (string ext; (ext = Str(r)) != "";)
                for (string dir; (dir = Str(r)) != "";)
                    for (string name; (name = Str(r)) != "";)
                    {
                        r.ReadUInt32(); // crc
                        ushort preload = r.ReadUInt16(), archive = r.ReadUInt16();
                        uint offset = r.ReadUInt32(), length = r.ReadUInt32();
                        r.ReadUInt16(); // terminator
                        entries[$"{dir}/{name}.{ext}"] = new(archive, offset, length, r.ReadBytes(preload));
                    }
        }

        public byte[]? Read(string path)
        {
            if (!entries.TryGetValue(path, out var e)) return null;
            if (e.Length == 0) return e.Preload;
            bool inDir = e.Archive == 0x7FFF;
            using var f = File.OpenRead(inDir ? dirFile : $"{prefix}{e.Archive:D3}.vpk");
            f.Position = (inDir ? dataStart : 0) + e.Offset;
            var data = new byte[e.Preload.Length + e.Length];
            e.Preload.CopyTo(data, 0);
            f.ReadExactly(data, e.Preload.Length, (int)e.Length);
            return data;
        }

        static string Str(BinaryReader r)
        {
            var bytes = new List<byte>();
            for (byte b; (b = r.ReadByte()) != 0;) bytes.Add(b);
            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}
