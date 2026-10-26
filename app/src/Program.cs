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

        if (args.Length >= 4 && args[0] == "--files-test")
        {
            // --files-test HOST PORT PATH...: send files to a files service and print what happens.
            try { FileSend.SendTo(args[1], int.Parse(args[2]), args[3..], Console.Error.WriteLine).GetAwaiter().GetResult(); Environment.Exit(0); }
            catch (Exception e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
        }

        if (args.Length >= 3 && args[0] == "--mouse-test")
        {
            Environment.Exit(DevTools.MouseTest(args[1], int.Parse(args[2])));
        }

        if (args.Length >= 4 && args[0] == "--vnc-snapshot")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Environment.Exit(DevTools.VncSnapshot(args[1], int.Parse(args[2]), args[3], args.Length > 4 ? int.Parse(args[4]) : 3));
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
