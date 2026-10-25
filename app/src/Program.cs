namespace Async;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--render-docs")
        {
            // Developer tool: writes the README images and the app icon (see DevTools.cs).
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            DevTools.RenderDocs(args[1]);
            return;
        }

        using var single = new Mutex(false, "Async.App");
        try
        {
            // Wait a moment so "Restart Async" can hand over to the new copy.
            if (!single.WaitOne(3000)) return;
        }
        catch (AbandonedMutexException) { }

        // Don't hold on to whatever folder we were started from (it may be a network share).
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Write($"Unhandled: {e.Exception}");
        Application.Run(new AsyncApp(args));
    }
}
