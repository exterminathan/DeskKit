// DeskKit.exe entry point (contract 7.4).
//   DeskKit.exe                         start the tray (one copy: mutex Local\DeskKit.Shell)
//   DeskKit.exe --run <id> [--action <id>]   start one module and exit at once, no tray
//   DeskKit.exe --run <id> -- <args>    the same, with <args> appended after the module's args
//   DeskKit.exe --stop <id>             ask a running module to stop with its own stop event
//   DeskKit.exe --list | --version | --selftest
//   DeskKit.exe --show                  open the main window (in the running tray, or at tray start)
//   DeskKit.exe --exit                  close the running tray (modules keep running)
//   DeskKit.exe --check-updates         print the update table, install nothing (contract 16.9)
//   DeskKit.exe --update <id|all|shell> [--yes]   install updates (--yes: stop a running module without asking)
//   --modules-dir <path>                use another modules folder (development: the repo's modules\)
using System.Reflection;

namespace DeskKit.Shell;

public static class Program
{
    public const string ShellMutexName = @"Local\DeskKit.Shell";

    public static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    internal sealed class Options
    {
        public string? Run, Action, Stop, ModulesDir, Update;
        public bool List, ShowVersion, SelfTest, Show, Exit, CheckUpdates, Yes;
        public List<string>? Extra;   // everything after a bare "--" (contract 7.4)
    }

    [STAThread]
    public static int Main(string[] args)
    {
        Options o;
        try { o = Parse(args); }
        catch (ArgumentException ex)
        {
            return Fail(2, ex.Message + "\nUsage: DeskKit.exe [--run <id> [--action <id> | -- <args>]] [--stop <id>] [--list] [--version] [--selftest] [--show] [--exit] [--check-updates] [--update <id|all|shell> [--yes]] [--modules-dir <path>]");
        }

        if (o.ShowVersion || o.List || o.SelfTest || o.CheckUpdates || o.Update != null) Native.AttachParentConsoleIfNeeded();
        if (o.ShowVersion) { Console.WriteLine($"DeskKit {Version}"); return 0; }
        if (o.SelfTest) return SelfTest.Run(o.ModulesDir);
        if (o.Exit) return SignalRunningShell(TrayApp.ExitEventName) ? 0 : Fail(1, "DeskKit is not running.", quiet: true);

        var modulesDir = ResolveModulesDir(o.ModulesDir);
        if (o.List) { Console.Write(ListText(modulesDir)); return 0; }
        if (o.CheckUpdates) return UpdateCli.Check(modulesDir);
        if (o.Update != null) return UpdateCli.Update(modulesDir, o.Update, o.Yes);
        if (o.Run != null) return RunOnce(modulesDir, o.Run, o.Action, o.Extra);
        if (o.Stop != null) return StopOnce(modulesDir, o.Stop);
        return StartTray(modulesDir, o.ModulesDir, o.Show);
    }

    internal static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length && !args[i + 1].StartsWith("--")
                ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            if (args[i] == "--")
            {
                o.Extra = args.Skip(i + 1).ToList();   // passed through unchanged, even if they look like options
                break;
            }
            switch (args[i])
            {
                case "--run": o.Run = Next(); break;
                case "--action": o.Action = Next(); break;
                case "--stop": o.Stop = Next(); break;
                case "--modules-dir": o.ModulesDir = Next(); break;
                case "--list": o.List = true; break;
                case "--version": o.ShowVersion = true; break;
                case "--selftest": o.SelfTest = true; break;
                case "--show": o.Show = true; break;
                case "--exit": o.Exit = true; break;
                case "--check-updates": o.CheckUpdates = true; break;
                case "--update": o.Update = Next(); break;
                case "--yes": o.Yes = true; break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        if (o.Action != null && o.Run == null) throw new ArgumentException("--action needs --run <id>.");
        if (o.Extra != null && o.Run == null) throw new ArgumentException("\"--\" needs --run <id>.");
        if (o.Extra != null && o.Action != null) throw new ArgumentException("\"--\" and --action do not combine.");
        if (o.Yes && o.Update == null) throw new ArgumentException("--yes needs --update.");
        return o;
    }

    /// <summary>
    /// --modules-dir, else &lt;exe dir&gt;\modules (the install layout). If that is missing, a parent folder that
    /// holds suite.json and modules\ (running from dist\shell in the repo).
    /// </summary>
    public static string ResolveModulesDir(string? arg)
    {
        if (arg != null) return Path.GetFullPath(arg).TrimEnd('\\');
        var exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        var beside = Path.Combine(exeDir, "modules");
        if (Directory.Exists(beside)) return beside;
        var dir = Directory.GetParent(exeDir);
        for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "suite.json")) && Directory.Exists(Path.Combine(dir.FullName, "modules")))
                return Path.Combine(dir.FullName, "modules");
        return beside;
    }

    // ---------------- --list ----------------

    public static string ListText(string modulesDir)
    {
        var modules = ModuleScanner.Scan(modulesDir);
        var rows = new List<string[]> { new[] { "ID", "VERSION", "MODE", "SOURCE", "STATE", "STATUS" } };
        foreach (var e in modules)
        {
            var m = e.Manifest;
            string state = "-";
            if (m != null && RunModes.IsLongRunning(m.RunMode) && m.Instance.Mutex != null)
                state = Native.MutexExists(m.Instance.Mutex) ? "running" : "stopped";
            rows.Add(new[]
            {
                e.Id, m?.Version ?? "-", m?.RunMode ?? "-", m?.Source ?? "-", m == null ? "broken" : state,
                Runtimes.Diagnose(e) ?? "ok",
            });
        }
        var widths = Enumerable.Range(0, 5).Select(c => rows.Max(r => r[c].Length)).ToArray();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"DeskKit {Version}: {modules.Count} module(s) in {modulesDir}");
        foreach (var r in rows)
        {
            for (int c = 0; c < 5; c++) sb.Append(r[c].PadRight(widths[c] + 2));
            sb.AppendLine(r[5]);
        }
        return sb.ToString();
    }

    // ---------------- --run, --stop ----------------

    internal static int RunOnce(string modulesDir, string id, string? actionId, IReadOnlyList<string>? extra = null)
    {
        var e = ModuleScanner.Scan(modulesDir).FirstOrDefault(m => m.Id == id);
        if (e == null) return Fail(1, $"DeskKit has no module \"{id}\" in {modulesDir}.");
        if (e.IsBroken) return Fail(1, $"{e.FolderName}: module.json is broken: {e.Error}");
        var m = e.Manifest!;
        IReadOnlyList<string> args = m.Args;
        if (actionId != null)
        {
            var a = m.Actions.FirstOrDefault(x => x.Id == actionId);
            if (a == null) return Fail(1, $"{m.Name} has no action \"{actionId}\".");
            args = a.Args;
        }
        try
        {
            using var p = Launcher.Start(e, args, extra);
            ShellLog.Write($"--run {id}{(actionId != null ? " --action " + actionId : "")}{(extra != null ? $" -- ({extra.Count} arg(s))" : "")}: pid {p.Id}");
            return 0;
        }
        catch (RuntimeMissingException ex) { return Fail(4, ex.Message); }
        catch (FileNotFoundException ex) { return Fail(1, ex.Message); }
        catch (Exception ex) { ShellLog.Write($"--run {id} failed: {ex}"); return Fail(1, $"{m.Name} could not start: {ex.Message}"); }
    }

    private static int StopOnce(string modulesDir, string id)
    {
        using var c = new ModuleController(modulesDir, ShellState.Open());
        var e = c.Find(id);
        if (e == null) return Fail(1, $"DeskKit has no module \"{id}\" in {modulesDir}.");
        var msg = c.StopAsync(e).GetAwaiter().GetResult();
        return msg == null ? 0 : Fail(1, msg);
    }

    // ---------------- Tray ----------------

    private static int StartTray(string modulesDir, string? modulesDirArg, bool show)
    {
        using var mutex = new Mutex(false, ShellMutexName, out var createdNew);
        if (!createdNew)
        {
            ShellLog.Write("second copy: asking the running shell to show its window, then exiting");
            SignalRunningShell(TrayApp.ShowEventName);
            return 0;
        }
        ShellLog.Write($"DeskKit {Version} tray start, exe {Environment.ProcessPath}");
        Application.ThreadException += (_, ev) => ShellLog.Write($"UI exception: {ev.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => ShellLog.Write($"unhandled: {ev.ExceptionObject}");
        ApplicationConfiguration.Initialize();
        using var controller = new ModuleController(modulesDir, ShellState.Open());
        using var app = new TrayApp(controller, modulesDirArg, show);
        Application.Run(app);
        ShellLog.Write("tray stopped");
        GC.KeepAlive(mutex);
        return 0;
    }

    private static bool SignalRunningShell(string eventName) => Native.SignalEvent(eventName);

    // ---------------- Errors ----------------

    /// <summary>Write "ERROR: message" to stderr and show it in a message box (unless DESKKIT_NO_UI=1).</summary>
    private static int Fail(int code, string message, bool quiet = false)
    {
        ShellLog.Write($"error (exit {code}): {message}");
        try { Console.Error.WriteLine($"ERROR: {message}"); } catch (Exception) { }
        if (!quiet && Environment.GetEnvironmentVariable("DESKKIT_NO_UI") != "1")
            MessageBox.Show(message, "DeskKit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return code;
    }
}
