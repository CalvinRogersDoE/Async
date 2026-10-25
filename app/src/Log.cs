namespace Async;

static class Log
{
    static readonly string FilePath = Path.Combine(Settings.Folder, "async.log");
    static readonly object Gate = new();

    static Log()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
        }
        catch { }
    }

    public static void Write(string line)
    {
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}"); } catch { }
        }
    }
}
