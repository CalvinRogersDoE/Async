using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Async;

/// <summary>
/// Files from a paired computer (dropped onto a portal, dragged across an edge, or dragged out of a
/// portal back to this PC) arrive through the tunnel's <c>files</c> service, which async-core hands to
/// 127.0.0.1:24851. This PC asks where they should go, starting at the folder picked last time, and
/// never overwrites what's already there. The wire format is described in desktop/src/files.rs.
/// </summary>
sealed class FileReceive : IDisposable
{
    public const int Port = 24851;
    readonly Settings cfg;
    readonly Control ui;
    readonly Action<string> tell;
    readonly TcpListener listener = new(IPAddress.Loopback, Port);

    public FileReceive(Settings cfg, Control ui, Action<string> tell)
    {
        this.cfg = cfg;
        this.ui = ui;
        this.tell = tell;
        try
        {
            listener.Start();
            _ = AcceptLoop();
        }
        catch (Exception e) { Log.Write($"Receiving files unavailable (port {Port} taken?): {e.Message}"); }
    }

    async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    try { await Receive(c.GetStream()); }
                    catch (Exception e)
                    {
                        Log.Write($"Receiving files failed: {e}");
                        tell($"Receiving files failed: {e.Message}");
                    }
                }
            });
        }
    }

    record Entry(string Path, long Size);

    async Task Receive(NetworkStream net)
    {
        int version = net.ReadByte();
        if (version < 0) return; // async-core checking whether the service is up
        if (version != 1) throw new IOException("the other computer runs a newer Async; update this one");
        var input = new BufferedStream(net, 1 << 20);
        string from = await ReadString(input);
        int count = (int)await ReadU32(input);
        var entries = new List<Entry>();
        for (int i = 0; i < count; i++) entries.Add(new Entry(await ReadString(input), (long)await ReadU64(input)));

        string what = What(entries);
        string? dest = (string?)ui.Invoke(() => PickFolder($"Where should {what} from {from} go?"));
        if (dest == null) { net.WriteByte(0); return; }
        var shown = Path.GetFileName(Path.TrimEndingDirectorySeparator(dest));
        if (string.IsNullOrEmpty(shown)) shown = dest;
        var answer = new MemoryStream();
        answer.WriteByte(1);
        WriteString(answer, shown);
        await net.WriteAsync(answer.ToArray());

        // Top-level names that already exist get " (2)", " (3)"… so nothing is overwritten.
        var renamed = new Dictionary<string, string>();
        var buffer = new byte[1 << 20];
        long total = 0;
        foreach (var e in entries)
        {
            var parts = e.Path.TrimEnd('/').Split('/');
            if (parts.Length == 0 || parts.Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new IOException($"refusing the path {e.Path}");
            if (!renamed.TryGetValue(parts[0], out var top)) renamed[parts[0]] = top = FreeName(dest, parts[0]);
            var target = Path.Combine([dest, top, .. parts[1..]]);
            if (e.Path.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            long left = e.Size;
            while (left > 0)
            {
                int n = (int)Math.Min(buffer.Length, left);
                await input.ReadExactlyAsync(buffer.AsMemory(0, n));
                await file.WriteAsync(buffer.AsMemory(0, n));
                left -= n;
            }
            total += e.Size;
        }
        net.WriteByte(1);
        Log.Write($"Received {entries.Count} entries from {from} into {dest}");
        tell($"Got {what} ({SizeText(total)}) from {from} in {shown}.");
    }

    /// <summary>Asks where the files go, on top of everything (a portal may be covering the screen).</summary>
    string? PickFolder(string prompt)
    {
        // Tests: a fixed folder instead of the dialog.
        if (Environment.GetEnvironmentVariable("ASYNC_TEST_DROP_FOLDER") is { Length: > 0 } fixedFolder) return fixedFolder;
        string start = Directory.Exists(cfg.LastDropFolder) ? cfg.LastDropFolder : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(1, 1) };
        owner.Show();
        Native.ForceForeground(owner.Handle);
        using var dlg = new FolderBrowserDialog { Description = prompt, UseDescriptionForTitle = true, InitialDirectory = start, SelectedPath = start, ShowNewFolderButton = true };
        if (dlg.ShowDialog(owner) != DialogResult.OK || !Directory.Exists(dlg.SelectedPath)) return null;
        cfg.LastDropFolder = dlg.SelectedPath;
        cfg.Save();
        return dlg.SelectedPath;
    }

    static string FreeName(string dest, string name)
    {
        if (!File.Exists(Path.Combine(dest, name)) && !Directory.Exists(Path.Combine(dest, name))) return name;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!File.Exists(Path.Combine(dest, candidate)) && !Directory.Exists(Path.Combine(dest, candidate))) return candidate;
        }
    }

    static string What(List<Entry> entries)
    {
        var tops = entries.Select(e => e.Path.Split('/')[0]).Distinct().ToList();
        if (tops.Count == 1) return entries.Any(e => e.Path.StartsWith(tops[0] + "/")) ? $"the folder “{tops[0]}”" : $"“{tops[0]}”";
        return $"{entries.Count(e => !e.Path.EndsWith('/'))} files";
    }

    static string SizeText(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b >> 10} KB",
        _ => $"{b} bytes",
    };

    static async Task<byte[]> ReadBytes(Stream s, int n) { var b = new byte[n]; await s.ReadExactlyAsync(b); return b; }
    static async Task<uint> ReadU32(Stream s) => BinaryPrimitives.ReadUInt32LittleEndian(await ReadBytes(s, 4));
    static async Task<ulong> ReadU64(Stream s) => BinaryPrimitives.ReadUInt64LittleEndian(await ReadBytes(s, 8));
    static async Task<string> ReadString(Stream s) => Encoding.UTF8.GetString(await ReadBytes(s, BinaryPrimitives.ReadUInt16LittleEndian(await ReadBytes(s, 2))));

    static void WriteString(Stream s, string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        if (b.Length > ushort.MaxValue) b = b[..ushort.MaxValue];
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)b.Length);
        s.Write(len);
        s.Write(b);
    }

    public void Dispose() => listener.Stop();
}
