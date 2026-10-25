using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Async;

/// <summary>A device this one is paired with, as async-core reports it.</summary>
sealed record Device(string Id, string Name);

/// <summary>
/// Runs async-core.exe (it ships next to Async.exe) and talks to it with one JSON object per line
/// over stdin/stdout. Replies and events are raised on the UI thread.
/// </summary>
sealed class CoreClient : IDisposable
{
    readonly Process process;
    readonly SynchronizationContext ui;
    readonly Dictionary<int, TaskCompletionSource<JsonObject>> pending = new();
    readonly object gate = new();
    int nextId;

    public string DeviceId { get; private set; } = "";
    public string DeviceName { get; private set; } = Environment.MachineName;
    public IReadOnlyList<Device> Devices { get; private set; } = [];

    /// <summary>Another device finished pairing with a code this one is showing.</summary>
    public event Action<Device>? Paired;
    /// <summary>A shown code expired or failed, with the reason.</summary>
    public event Action<string>? PairEnded;
    public event Action? DevicesChanged;

    public CoreClient(string? dataDir = null)
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var exe = Path.Combine(AppContext.BaseDirectory, "async-core.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("async-core.exe is missing from the Async folder.", exe);
        process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, ["--data", dataDir ?? Path.Combine(Settings.Folder, "core"), "control"])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            },
            EnableRaisingEvents = true,
        };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write($"core: {e.Data}"); };
        process.Exited += (_, _) => Log.Write($"async-core exited ({process.ExitCode})");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // async-core exits on its own when its stdin closes, which also happens if Async crashes.
        _ = RefreshDevices();
    }

    // ---------- commands ----------

    public async Task RefreshDevices()
    {
        var reply = await Request("peers");
        Devices = reply["peers"]!.AsArray()
            .Select(p => new Device((string)p!["id"]!, (string)p["name"]!))
            .ToList();
        DevicesChanged?.Invoke();
    }

    /// <summary>Starts showing a pairing code. Watch <see cref="Paired"/> / <see cref="PairEnded"/>.</summary>
    public async Task<string> ShowPairingCode() => (string)(await Request("pair_host"))["code"]!;

    public Task CancelPairing() => Request("pair_cancel");

    public async Task<Device> PairWithCode(string code)
    {
        var p = (await Request("pair_join", new() { ["code"] = code }))["peer"]!;
        await RefreshDevices();
        return new Device((string)p["id"]!, (string)p["name"]!);
    }

    public async Task Remove(string deviceId)
    {
        await Request("remove_peer", new() { ["peer"] = deviceId });
        await RefreshDevices();
    }

    /// <summary>
    /// A local address whose traffic comes out at the device's screen sharing, and which kind it is
    /// ("rdp" or "vnc"). The core picks Remote Desktop when the device offers both.
    /// </summary>
    public async Task<(string Host, int Port, string Service)> OpenScreen(string deviceId)
    {
        var r = await Request("tunnel", new() { ["peer"] = deviceId });
        return ((string)r["host"]!, (int)r["port"]!, (string)r["service"]!);
    }

    /// <summary>What the device is (its OS) and which screen sharing it has turned on.</summary>
    public async Task<(string Os, string[] Services)> Describe(string deviceId)
    {
        var r = await Request("describe", new() { ["peer"] = deviceId });
        return ((string)r["os"]!, r["services"]!.AsArray().Select(s => (string)s!).ToArray());
    }

    public async Task<(int RttMs, bool Direct)> Probe(string deviceId)
    {
        var r = await Request("probe", new() { ["peer"] = deviceId });
        return ((int)r["rtt_ms"]!, (bool)r["direct"]!);
    }

    async Task<JsonObject> Request(string cmd, JsonObject? fields = null)
    {
        var msg = fields ?? new JsonObject();
        var done = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            int id = ++nextId;
            msg["id"] = id;
            msg["cmd"] = cmd;
            pending[id] = done;
            process.StandardInput.WriteLine(msg.ToJsonString());
            process.StandardInput.Flush();
        }
        var reply = await done.Task;
        if ((bool?)reply["ok"] != true) throw new CoreException((string?)reply["error"] ?? "Something went wrong.");
        return reply;
    }

    // ---------- replies and events ----------

    void OnLine(string line)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { return; }
        if (msg == null) return;

        if (msg["id"] is JsonValue idValue && idValue.TryGetValue(out int id))
        {
            TaskCompletionSource<JsonObject>? done;
            lock (gate) { if (pending.Remove(id, out done)) { } }
            done?.TrySetResult(msg);
            return;
        }
        ui.Post(_ => OnEvent(msg), null);
    }

    void OnEvent(JsonObject msg)
    {
        switch ((string?)msg["event"])
        {
            case "ready":
                DeviceId = (string?)msg["node_id"] ?? "";
                DeviceName = (string?)msg["name"] ?? DeviceName;
                break;
            case "paired":
                var p = msg["peer"]!;
                var device = new Device((string)p["id"]!, (string)p["name"]!);
                _ = RefreshDevices();
                Paired?.Invoke(device);
                break;
            case "pair_ended":
                PairEnded?.Invoke((string?)msg["error"] ?? "Pairing ended.");
                break;
        }
    }

    public void Dispose()
    {
        try { process.StandardInput.Close(); } catch { }
        if (!process.WaitForExit(1500)) try { process.Kill(); } catch { }
        process.Dispose();
    }
}

sealed class CoreException(string message) : Exception(message);
