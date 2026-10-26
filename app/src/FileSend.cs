using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Async;

/// <summary>
/// Sends files and folders to a paired computer through the tunnel's <c>files</c> service. The other
/// computer asks where they should go, so they end up where the person wants them. The wire format
/// is described in desktop/src/files.rs.
/// </summary>
static class FileSend
{
    record Entry(string Relative, string? FullPath, long Size);

    /// <summary>Sends <paramref name="paths"/>; reports progress and the outcome through <paramref name="tell"/>.</summary>
    public static async Task Send(CoreClient core, string device, string deviceName, string[] paths, Action<string> tell)
    {
        try
        {
            var entries = await Task.Run(() => Collect(paths));
            if (entries.Count == 0) return;
            var (host, port, _) = await core.OpenService(device, "files");
            await SendTo(host, port, deviceName, entries, tell);
        }
        catch (Exception e)
        {
            Log.Write($"Sending files failed: {e}");
            tell($"Couldn't send to {deviceName}: {e.Message}");
        }
    }

    /// <summary>For tests: sends straight to a files service at host:port.</summary>
    internal static Task SendTo(string host, int port, string[] paths, Action<string> tell) => SendTo(host, port, "test", Collect(paths), tell);

    static async Task SendTo(string host, int port, string deviceName, List<Entry> entries, Action<string> tell)
    {
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port);
            var net = client.GetStream();

            var head = new MemoryStream();
            head.WriteByte(1);
            WriteString(head, Environment.MachineName);
            Span<byte> n = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(n, (uint)entries.Count);
            head.Write(n[..4]);
            foreach (var e in entries)
            {
                WriteString(head, e.Relative);
                BinaryPrimitives.WriteUInt64LittleEndian(n, (ulong)e.Size);
                head.Write(n);
            }
            await net.WriteAsync(head.ToArray());
            tell($"{deviceName} is asking where to put {What(entries)}…");

            // Waits as long as the person takes to pick a folder over there.
            int answer = net.ReadByte();
            if (answer != 1) { if (answer == 0) tell($"{deviceName} didn't take {What(entries)}."); return; }
            var lenBytes = new byte[2];
            await net.ReadExactlyAsync(lenBytes);
            var folderBytes = new byte[BinaryPrimitives.ReadUInt16LittleEndian(lenBytes)];
            await net.ReadExactlyAsync(folderBytes);
            string folder = Encoding.UTF8.GetString(folderBytes);

            var buffer = new byte[1 << 20];
            foreach (var e in entries.Where(e => e.FullPath != null))
            {
                await using var file = new FileStream(e.FullPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
                long left = e.Size;
                while (left > 0)
                {
                    int got = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
                    if (got <= 0) throw new IOException($"{Path.GetFileName(e.FullPath)} got shorter while it was being sent");
                    await net.WriteAsync(buffer.AsMemory(0, got));
                    left -= got;
                }
            }
            if (net.ReadByte() != 1) throw new IOException("the other computer stopped partway");
            tell($"Sent {What(entries)} to {folder} on {deviceName}.");
            Log.Write($"Sent {entries.Count} entries to {deviceName} ({folder})");
        }
    }

    /// <summary>Every file and folder under the dropped paths, named relative to where they were dropped from.</summary>
    static List<Entry> Collect(string[] paths)
    {
        var list = new List<Entry>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) list.Add(new Entry(Path.GetFileName(p), p, new FileInfo(p).Length));
            else if (Directory.Exists(p))
            {
                var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(p)) ?? "";
                string Rel(string full) => Path.GetRelativePath(root, full).Replace('\\', '/');
                list.Add(new Entry(Rel(p) + "/", null, 0));
                foreach (var d in SafeEnumerate(p, dirs: true)) list.Add(new Entry(Rel(d) + "/", null, 0));
                foreach (var f in SafeEnumerate(p, dirs: false))
                {
                    try { list.Add(new Entry(Rel(f), f, new FileInfo(f).Length)); } catch { }
                }
            }
        }
        return list;
    }

    static IEnumerable<string> SafeEnumerate(string dir, bool dirs)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        return dirs ? Directory.EnumerateDirectories(dir, "*", options) : Directory.EnumerateFiles(dir, "*", options);
    }

    static string What(List<Entry> entries)
    {
        var tops = entries.Select(e => e.Relative.Split('/')[0]).Distinct().ToList();
        if (tops.Count == 1) return entries.Any(e => e.Relative.EndsWith('/')) ? $"the folder “{tops[0]}”" : $"“{tops[0]}”";
        return $"{entries.Count(e => e.FullPath != null)} files";
    }

    static void WriteString(Stream s, string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        if (b.Length > ushort.MaxValue) b = b[..ushort.MaxValue];
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)b.Length);
        s.Write(len);
        s.Write(b);
    }
}

/// <summary>
/// Catches files dragged (in Explorer, on the desktop…) up to an edge. While a mouse button is held,
/// a nearly invisible strip lies along each mouse-mode edge; a file drag reaching it tells Async
/// what's being dragged, so the drag can carry on over to the other computer. While another
/// computer is showing this one in a portal, strips also lie along the bottom (the portal's way
/// back): files dropped there go back to that computer.
/// </summary>
sealed class DragStrips : IDisposable
{
    readonly Settings cfg;
    readonly List<Strip> strips = [];
    readonly System.Windows.Forms.Timer poll = new() { Interval = 60 };

    /// <summary>The files being dragged over a strip right now, or null.</summary>
    public string[]? Files { get; private set; }
    /// <summary>Whether a paired computer is showing this one in a portal right now.</summary>
    public Func<bool>? Viewed;
    /// <summary>Files were dropped on the bottom edge while being viewed: send them to the viewer.</summary>
    public event Action<string[]>? DroppedOut;

    public DragStrips(Settings cfg)
    {
        this.cfg = cfg;
        poll.Tick += (_, _) => Update();
        poll.Start();
    }

    void Update()
    {
        bool held = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (!held)
        {
            if (strips.Count > 0) Hide();
            return;
        }
        if (strips.Count > 0) return;
        bool viewed = Viewed?.Invoke() == true;
        foreach (var s in Screen.AllScreens)
            foreach (var e in Edges.All)
            {
                bool outward = e == Edge.Bottom ? viewed : cfg.IsMouseEdge(s.DeviceName, e) && cfg.DeviceFor(s.DeviceName, e) != null;
                if (!outward || !EdgeWatcher.IsFree(s.Bounds, e)) continue;
                var b = s.Bounds;
                const int t = 3;
                var r = e switch
                {
                    Edge.Top => new Rectangle(b.Left, b.Top, b.Width, t),
                    Edge.Bottom => new Rectangle(b.Left, b.Bottom - t, b.Width, t),
                    Edge.Left => new Rectangle(b.Left, b.Top, t, b.Height),
                    _ => new Rectangle(b.Right - t, b.Top, t, b.Height),
                };
                var strip = new Strip(this, sendsBack: e == Edge.Bottom) { Bounds = r };
                strip.Show();
                strip.Bounds = r;
                strips.Add(strip);
            }
    }

    void Hide()
    {
        foreach (var s in strips) s.Dispose();
        strips.Clear();
        Files = null;
    }

    sealed class Strip : Form
    {
        readonly DragStrips owner;
        readonly bool sendsBack;
        public Strip(DragStrips owner, bool sendsBack)
        {
            this.owner = owner;
            this.sendsBack = sendsBack;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Opacity = 0.01;
            BackColor = Color.Black;
            AllowDrop = true;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } // WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW
        }
        protected override void OnDragEnter(DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                owner.Files = files;
                e.Effect = DragDropEffects.Copy;
            }
        }
        protected override void OnDragOver(DragEventArgs e) => e.Effect = owner.Files != null ? DragDropEffects.Copy : DragDropEffects.None;
        protected override void OnDragLeave(EventArgs e) { /* keep Files: leaving the strip is how the drag crosses over */ }
        protected override void OnDragDrop(DragEventArgs e)
        {
            // On a mouse-mode edge this is the drop Async causes itself once the pointer has crossed.
            // On the bottom edge of a screen someone is viewing, it sends the files back to them.
            if (sendsBack && e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                owner.DroppedOut?.Invoke(files);
        }
    }

    public void Dispose()
    {
        poll.Dispose();
        Hide();
    }
}
