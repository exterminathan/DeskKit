// Drives modules for the tray and the main window: start, toggle, the stop protocol (contract 7.3),
// the oneshot time limit, crash notices (7.4) and autostart. UI-free, so the self-test can use it.
using System.Diagnostics;

namespace DeskKit.Shell;

public sealed class ModuleController : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Process> _own = new(StringComparer.Ordinal);   // processes this shell started
    private readonly HashSet<int> _stopping = new();

    public string ModulesDir { get; }
    public ShellState State { get; }
    public List<ModuleEntry> Modules { get; private set; } = new();
    public TimeSpan OneshotTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A notice for the user: title, text, and whether it is an error. Raised on any thread.</summary>
    public event Action<string, string, bool>? Notify;

    public ModuleController(string modulesDir, ShellState state)
    {
        ModulesDir = modulesDir;
        State = state;
        Reload();
    }

    public void Reload()
    {
        Modules = ModuleScanner.Scan(ModulesDir);
        ShellLog.Write($"scanned {ModulesDir}: {Modules.Count} module(s): {string.Join(", ", Modules.Select(m => m.Id + (m.IsBroken ? " (broken)" : "")))}");
    }

    public ModuleEntry? Find(string id) => Modules.FirstOrDefault(m => m.Id == id);

    // ---------------- State ----------------

    public bool OwnsLiveProcess(string id)
    {
        lock (_lock)
        {
            if (!_own.TryGetValue(id, out var p)) return false;
            try { return !p.HasExited; } catch (Exception) { return false; }
        }
    }

    public int? OwnProcessId(string id)
    {
        lock (_lock) return _own.TryGetValue(id, out var p) && OwnsLiveProcess(id) ? p.Id : null;
    }

    public bool IsRunning(ModuleEntry e)
    {
        if (e.IsBroken) return false;
        var m = e.Manifest!;
        return RunModes.IsRunning(m.RunMode, m.Instance.Mutex, Native.MutexExists, OwnsLiveProcess(m.Id));
    }

    public string StateText(ModuleEntry e)
    {
        if (e.IsBroken) return "broken";
        var running = IsRunning(e);
        var m = e.Manifest!;
        if (!running) return "stopped";
        if (RunModes.IsLongRunning(m.RunMode) && !OwnsLiveProcess(m.Id)) return "running (not started by DeskKit)";
        return "running";
    }

    // ---------------- Start, toggle, stop ----------------

    /// <summary>Start with args, or with an action's args. Returns an error message, or null on success.</summary>
    public string? Start(ModuleEntry e, string? actionId = null)
    {
        if (e.IsBroken) return $"{e.FolderName}: module.json is broken: {e.Error}";
        var m = e.Manifest!;
        IReadOnlyList<string> args = m.Args;
        if (actionId != null)
        {
            var action = m.Actions.FirstOrDefault(a => a.Id == actionId);
            if (action == null) return $"{m.Name} has no action \"{actionId}\".";
            args = action.Args;
        }
        Process p;
        try { p = Launcher.Start(e, args); }
        catch (RuntimeMissingException ex) { return ex.Message; }
        catch (FileNotFoundException ex) { return ex.Message; }
        catch (Exception ex) { ShellLog.Write($"start {m.Id} failed: {ex}"); return $"{m.Name} could not start: {ex.Message}"; }

        lock (_lock)
        {
            // Keep the first live copy for tray/window: a second start hands over to it and exits 0.
            if (!(RunModes.IsLongRunning(m.RunMode) && OwnsLiveProcess(m.Id))) _own[m.Id] = p;
        }
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => OnExited(e, p);
        if (m.RunMode == "oneshot") WatchOneshot(e, p);
        return null;
    }

    public async Task<string?> ToggleAsync(ModuleEntry e)
    {
        if (e.IsBroken) return $"{e.FolderName}: module.json is broken: {e.Error}";
        var m = e.Manifest!;
        switch (RunModes.Toggle(m.RunMode, IsRunning(e), m.Actions.Any(a => a.Id == "toggle")))
        {
            case ToggleKind.Stop: return await StopAsync(e);
            case ToggleKind.StartToggleAction: return Start(e, "toggle");
            default: return Start(e);
        }
    }

    /// <summary>Stop protocol (contract 7.3). Returns a message for the user, or null when the module stopped.</summary>
    public async Task<string?> StopAsync(ModuleEntry e)
    {
        if (e.IsBroken) return null;
        var m = e.Manifest!;
        if (!RunModes.StopOffered(m.RunMode)) return $"{m.Name} is a {m.RunMode} module: it exits by itself.";
        Process? own;
        lock (_lock) own = _own.TryGetValue(m.Id, out var p) && OwnsLiveProcess(m.Id) ? p : null;
        if (!IsRunning(e) && own == null) return null;
        if (own != null) lock (_lock) _stopping.Add(own.Id);

        try
        {
            // Step 1: the module's own stop event. Works for any copy; it is the module's graceful exit.
            if (m.Instance.StopEvent != null && Native.SignalEvent(m.Instance.StopEvent))
            {
                ShellLog.Write($"stop {m.Id}: step 1, stop event set");
                if (await WaitStoppedAsync(e, own)) return null;
            }

            // Steps 2 and 3 only for a process this shell started.
            if (own == null)
                return m.Instance.StopEvent != null && IsRunning(e)
                    ? $"{m.Name} did not stop within {StopWait.TotalSeconds:0} seconds."
                    : RunModes.NotStartedByShellMessage(m.Name);

            var closed = Native.PostCloseToWindows(Native.ProcessTree(own.Id));
            ShellLog.Write($"stop {m.Id}: step 2, WM_CLOSE to {closed} window(s)");
            if (closed > 0 && await WaitStoppedAsync(e, own)) return null;

            ShellLog.Write($"stop {m.Id}: step 3, kill process tree {own.Id}");
            try { own.Kill(entireProcessTree: true); } catch (Exception ex) { ShellLog.Write($"kill failed: {ex.Message}"); }
            await WaitStoppedAsync(e, own);
            return null;
        }
        finally
        {
            // OnExited clears the mark for a process that ended; clear it here only if it is still alive.
            if (own != null && !HasExited(own)) lock (_lock) _stopping.Remove(own.Id);
        }
    }

    /// <summary>Poll every 250 ms for up to StopWait: the mutex is gone (or, with no mutex, our process ended).</summary>
    private async Task<bool> WaitStoppedAsync(ModuleEntry e, Process? own)
    {
        var mutex = e.Manifest!.Instance.Mutex;
        var until = DateTime.UtcNow + StopWait;
        while (true)
        {
            bool ownDone = own == null || HasExited(own);
            bool mutexGone = mutex == null || !Native.MutexExists(mutex);
            if (mutexGone && (mutex != null || ownDone)) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(250);
        }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch (Exception) { return true; }
    }

    private void WatchOneshot(ModuleEntry e, Process p)
    {
        var limit = OneshotTimeout;
        Task.Run(async () =>
        {
            await Task.Delay(limit);
            if (HasExited(p)) return;
            ShellLog.Write($"oneshot {e.Id} still running after {limit.TotalSeconds:0} s: killing process tree {p.Id}");
            lock (_lock) _stopping.Add(p.Id);
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            Notify?.Invoke(e.DisplayName, RunModes.OneshotTimeoutMessage(e.DisplayName), true);
        });
    }

    private void OnExited(ModuleEntry e, Process p)
    {
        int code;
        try { code = p.ExitCode; } catch (Exception) { code = -1; }
        bool stopping;
        lock (_lock) stopping = _stopping.Remove(p.Id);
        ShellLog.Write($"exit {e.Id}: pid {p.Id} code {code}{(stopping ? " (stopped by shell)" : "")}");
        if (code == 0 || stopping) return;
        var m = e.Manifest!;
        if (m.Console) { Notify?.Invoke(m.Name, $"{m.Name} exited with code {code}.", true); return; }
        var line = Launcher.LastErrorLine(m.Id) ?? $"{m.Name} exited with code {code}.";
        if (code == 3)
        {
            var missing = m.Helpers.Where(h => Runtimes.DetectHelper(e, h) == null).Select(h => h.Name).ToList();
            if (missing.Count > 0)
                line += $" Use \"Download {string.Join("\" or \"Download ", missing)}\" in the DeskKit menu.";
        }
        Notify?.Invoke(m.Name, line, true);
    }

    // ---------------- Settings and folders (contract 7.4) ----------------

    /// <summary>"Settings" is shown when settings_action or settings_path is set.</summary>
    public static bool HasSettings(ModuleEntry e) =>
        !e.IsBroken && (e.Manifest!.SettingsAction != null || e.Manifest.SettingsPath != null);

    public string? OpenSettings(ModuleEntry e)
    {
        var m = e.Manifest!;
        if (m.SettingsAction != null) return Start(e, m.SettingsAction);
        var path = Runtimes.SettingsPath(e);
        if (path == null) return $"{m.Name} has no settings.";
        try
        {
            if (File.Exists(path)) ShellOpen(path);
            else
            {
                var dir = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(dir);
                ShellOpen(dir);
            }
            return null;
        }
        catch (Exception ex) { return $"Cannot open {path}: {ex.Message}"; }
    }

    /// <summary>Open the folder that holds the settings file (or the data dir); create it first.</summary>
    public string? OpenSettingsFolder(ModuleEntry e)
    {
        var dir = Runtimes.SettingsPath(e) is { } sp ? Path.GetDirectoryName(sp)! : Runtimes.ExpandDataDir(e);
        try { Directory.CreateDirectory(dir); ShellOpen(dir); return null; }
        catch (Exception ex) { return $"Cannot open {dir}: {ex.Message}"; }
    }

    public string? OpenLog(ModuleEntry e)
    {
        var log = Launcher.LogFile(e.Id);
        try
        {
            if (File.Exists(log)) ShellOpen(log);
            else { Directory.CreateDirectory(ShellPaths.ModuleLogsDir); ShellOpen(ShellPaths.ModuleLogsDir); }
            return null;
        }
        catch (Exception ex) { return $"Cannot open {log}: {ex.Message}"; }
    }

    public static void ShellOpen(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    // ---------------- Autostart (contract 7.4) ----------------

    public bool IsAutostartOn(ModuleEntry e) => !e.IsBroken && State.IsAutostartOn(e.Manifest!);

    /// <summary>Text for the autostart column: on, off, n/a, or the module's own state.</summary>
    public string AutostartText(ModuleEntry e)
    {
        if (e.IsBroken) return "";
        var m = e.Manifest!;
        if (!RunModes.AutostartAllowed(m.RunMode)) return "n/a";
        if (m.Autostart.ManagedBy == "module")
            return LoginEntry.ModuleOwnEntryOn(e) switch
            {
                true => "on (own entry)",
                false => "off (own entry)",
                null => "managed by module",
            };
        return IsAutostartOn(e) ? "on" : "off";
    }

    public bool AutostartEditable(ModuleEntry e) =>
        !e.IsBroken && RunModes.AutostartAllowed(e.Manifest!.RunMode) && e.Manifest.Autostart.ManagedBy == "shell";

    /// <summary>Start every tray/window module with managed_by shell and autostart on, if not already running.</summary>
    public void StartAutostartModules()
    {
        foreach (var e in Modules.Where(AutostartEditable).Where(IsAutostartOn))
        {
            if (IsRunning(e)) { ShellLog.Write($"autostart {e.Id}: already running"); continue; }
            var err = Start(e);
            ShellLog.Write($"autostart {e.Id}: {err ?? "started"}");
            if (err != null) Notify?.Invoke(e.DisplayName, err, true);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var p in _own.Values) p.Dispose();   // modules keep running: no module needs the shell
            _own.Clear();
        }
    }
}
