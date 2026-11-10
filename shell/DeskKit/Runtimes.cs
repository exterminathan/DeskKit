// How a module starts (contract 4, 5, 8.2): path tokens, finding Python, PowerShell and .NET,
// helper detection, and the command line per runtime.
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace DeskKit.Shell;

/// <summary>Shell paths. %APPDATA% and %LOCALAPPDATA% come from the environment, so tests can point them at temp.</summary>
public static class ShellPaths
{
    public static string AppData =>
        Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } a ? a
        : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string LocalAppData =>
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } a ? a
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string ShellDataDir => Path.Combine(AppData, "DeskKit", "shell");
    public static string StateFile => Path.Combine(ShellDataDir, "state.json");
    public static string ModuleLogsDir => Path.Combine(ShellDataDir, "logs");
    public static string ShellLogFile => Path.Combine(ShellDataDir, "shell.log");
    public static string HelpersDir => Path.Combine(LocalAppData, "DeskKit", "helpers");
}

/// <summary>A start the shell cannot make; Message is the exact text to show (contract 5.1, 5.2).</summary>
public sealed class RuntimeMissingException(string message) : Exception(message);

public sealed record StartCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDir, bool NewConsole)
{
    public string CommandLine => CommandLineBuilder.Build(FileName, Arguments);
}

public static class CommandLineBuilder
{
    /// <summary>Quote like ProcessStartInfo.ArgumentList (MSVCRT rules), so every element arrives unchanged.</summary>
    public static string Build(string fileName, IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        Append(sb, fileName);
        foreach (var a in args) { sb.Append(' '); Append(sb, a); }
        return sb.ToString();
    }

    public static void Append(StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) { sb.Append(arg); return; }
        sb.Append('"');
        for (int i = 0; i < arg.Length; i++)
        {
            int slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', slashes * 2); break; }
            if (arg[i] == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
            else { sb.Append('\\', slashes); sb.Append(arg[i]); }
        }
        sb.Append('"');
    }
}

public static class Runtimes
{
    // ---------------- Tokens (contract 4) ----------------

    public static string ExpandDataDir(ModuleEntry e) =>
        Normalize(Environment.ExpandEnvironmentVariables(ReplaceTokens(e.Manifest!.DataDir, e.Folder, null)));

    /// <summary>Tokens first, then environment variables.</summary>
    public static string Expand(string value, ModuleEntry e) =>
        Environment.ExpandEnvironmentVariables(ReplaceTokens(value, e.Folder, ExpandDataDir(e)));

    private static string ReplaceTokens(string value, string moduleDir, string? dataDir)
    {
        var s = value.Replace("{module_dir}", moduleDir).Replace("{helpers_dir}", ShellPaths.HelpersDir);
        if (dataDir != null) s = s.Replace("{data_dir}", dataDir);
        return s;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    public static string EntryPath(ModuleEntry e) => Path.GetFullPath(Path.Combine(e.Folder, e.Manifest!.Entry.Replace('/', '\\')));

    public static string? SettingsPath(ModuleEntry e) =>
        e.Manifest!.SettingsPath is { } p ? Normalize(Expand(p, e)) : null;

    // ---------------- Python (contract 5.2) ----------------

    public sealed record PythonInstall(string PythonExe, string? PythonwExe, Version Version);

    private static List<PythonInstall>? _pythonCache;
    private static readonly object PythonLock = new();

    /// <summary>Test hook: replace the found Python installs (null = search again).</summary>
    public static void SetPythonCache(List<PythonInstall>? installs) { lock (PythonLock) _pythonCache = installs; }

    /// <summary>Candidates in contract order: the py launcher first, then python.exe on PATH (no Store stub). Cached.</summary>
    public static List<PythonInstall> PythonInstalls()
    {
        lock (PythonLock)
        {
            if (_pythonCache != null) return _pythonCache;
            var found = new List<PythonInstall>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The py launcher is found on PATH like python.exe, so a PATH without Python means "not found".
            var py = FindOnPath("py.exe");
            var fromPy = py == null ? null : RunCapture(py, new[] { "-3", "-c", "import sys; print(sys.executable)" });
            if (fromPy != null) TryAdd(fromPy.Trim(), found, seen);
            foreach (var p in FindAllOnPath("python.exe"))
            {
                if (p.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase)) continue;
                TryAdd(p, found, seen);
            }
            _pythonCache = found;
            return found;
        }
    }

    private static void TryAdd(string pythonExe, List<PythonInstall> found, HashSet<string> seen)
    {
        if (!File.Exists(pythonExe) || !seen.Add(pythonExe)) return;
        var v = RunCapture(pythonExe, new[] { "-c", "import sys; print('%d.%d.%d' % sys.version_info[:3])" });
        if (v == null || !Version.TryParse(v.Trim(), out var version)) return;
        var w = Path.Combine(Path.GetDirectoryName(pythonExe)!, "pythonw.exe");
        found.Add(new PythonInstall(pythonExe, File.Exists(w) ? w : null, version));
    }

    public static PythonInstall? FindPython(string min) =>
        PythonInstalls().FirstOrDefault(p => p.Version >= ParseMin(min));

    public static string PythonMissingMessage(string min) =>
        $"Python {min} or newer not found. Install it from https://www.python.org/downloads/windows/ and try again.";

    // ---------------- PowerShell (contract 5.1) ----------------

    public static string PowerShellExe(string min)
    {
        if (ParseMin(min).Major >= 7)
            return FindOnPath("pwsh.exe") ?? throw new RuntimeMissingException(
                $"PowerShell {min} or newer not found. Install it from https://aka.ms/powershell and try again.");
        var root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        return Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    // ---------------- .NET (contract 5.1) ----------------

    private static string? _dotnetRuntimes;

    public static bool HasDesktopRuntime(string min)
    {
        _dotnetRuntimes ??= RunCapture("dotnet", new[] { "--list-runtimes" }) ?? "";
        var need = ParseMin(min);
        foreach (var line in _dotnetRuntimes.Split('\n'))
        {
            var parts = line.Trim().Split(' ');
            if (parts.Length >= 2 && parts[0] == "Microsoft.WindowsDesktop.App"
                && Version.TryParse(parts[1].Split('-')[0], out var v) && v >= need) return true;
        }
        return false;
    }

    public static string DotnetMissingMessage(string name, string min) =>
        $"{name} needs the .NET {min} Desktop Runtime. Install it from https://dotnet.microsoft.com/download/dotnet/{min} and try again.";

    // ---------------- Command per runtime (contract 5.1) ----------------

    /// <summary>
    /// Build the start command: the module's args with tokens and variables expanded, then the extra args
    /// exactly as given (they come from the user, so "{data_dir}" or "%APPDATA%" in them stays literal).
    /// Throws RuntimeMissingException with the contract's message.
    /// </summary>
    public static StartCommand BuildCommand(ModuleEntry e, IReadOnlyList<string> rawArgs, IReadOnlyList<string>? extraArgs = null)
    {
        var m = e.Manifest!;
        var args = rawArgs.Select(a => Expand(a, e)).Concat(extraArgs ?? Array.Empty<string>()).ToList();
        var entry = EntryPath(e);
        switch (m.Runtime.Type)
        {
            case "dotnet":
                if (m.Runtime.SelfContained == false && !HasDesktopRuntime(m.Runtime.Min))
                    throw new RuntimeMissingException(DotnetMissingMessage(m.Name, m.Runtime.Min));
                return new StartCommand(entry, args, e.Folder, m.Console);
            case "native":
                return new StartCommand(entry, args, e.Folder, m.Console);
            case "python":
            {
                var py = FindPython(m.Runtime.Min) ?? throw new RuntimeMissingException(PythonMissingMessage(m.Runtime.Min));
                var exe = m.Console ? py.PythonExe : py.PythonwExe ?? py.PythonExe;
                return new StartCommand(exe, new[] { entry }.Concat(args).ToList(), e.Folder, m.Console);
            }
            case "powershell":
            {
                var exe = PowerShellExe(m.Runtime.Min);
                var list = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass" };
                if (!m.Console) list.AddRange(new[] { "-NonInteractive", "-WindowStyle", "Hidden" });
                list.Add("-File");
                list.Add(entry);
                list.AddRange(args);
                return new StartCommand(exe, list, e.Folder, m.Console);
            }
            default:
                throw new RuntimeMissingException($"{m.Name}: runtime \"{m.Runtime.Type}\" is not supported.");
        }
    }

    // ---------------- Helpers (contract 8.2) ----------------

    /// <summary>The first file the detect rule finds, or null.</summary>
    public static string? DetectHelper(ModuleEntry e, HelperInfo h)
    {
        if (h.Detect.Setting is { Length: > 0 } key && SettingsPath(e) is { } sp && File.Exists(sp))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(sp)) is JsonObject o && o[key] is JsonValue v
                    && v.TryGetValue<string>(out var path) && path.Length > 0 && File.Exists(path)) return path;
            }
            catch (Exception) { /* a bad settings file means "not set" here; the module reports it */ }
        }
        foreach (var raw in h.Detect.Paths)
            if (MatchPath(Expand(raw, e)) is { } hit) return hit;
        if (h.Detect.OnPath is { Length: > 0 } file) return FindOnPath(file);
        return null;
    }

    /// <summary>One "*" path part stands for exactly one folder name; the last match in sorted order wins.</summary>
    public static string? MatchPath(string pattern)
    {
        var parts = pattern.Replace('/', '\\').Split('\\');
        int star = Array.IndexOf(parts, "*");
        if (star < 0) return File.Exists(pattern) ? Path.GetFullPath(pattern) : null;
        var prefix = string.Join('\\', parts.Take(star));
        var suffix = string.Join('\\', parts.Skip(star + 1));
        if (!Directory.Exists(prefix + "\\")) return null;
        return Directory.GetDirectories(prefix + "\\")
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(d => Path.Combine(d, suffix))
            .LastOrDefault(File.Exists);
    }

    // ---------------- Diagnosis for the list (main window, --list) ----------------

    /// <summary>A clear line when the module cannot start: runtime missing, entry missing, required helper missing.</summary>
    public static string? Diagnose(ModuleEntry e)
    {
        if (e.IsBroken) return $"Broken module.json: {e.Error}";
        var m = e.Manifest!;
        if (!File.Exists(EntryPath(e)))
            return m.Source == "pinned"
                ? $"Not installed: {m.Entry} is missing (the suite build downloads the pinned release)."
                : $"Not built: {m.Entry} is missing.";
        try { BuildCommand(e, m.Args); }
        catch (RuntimeMissingException ex) { return ex.Message; }
        foreach (var h in m.Helpers.Where(h => h.Required))
            if (DetectHelper(e, h) == null)
                return $"Needs {h.Name} ({h.Purpose}). Get it from {h.Download.Page}";
        return null;
    }

    /// <summary>Optional helpers that are missing, for an info line.</summary>
    public static string? OptionalHelperNote(ModuleEntry e)
    {
        if (e.IsBroken) return null;
        var missing = e.Manifest!.Helpers.Where(h => !h.Required && DetectHelper(e, h) == null).ToList();
        return missing.Count == 0 ? null
            : "Optional, missing: " + string.Join("; ", missing.Select(h => $"{h.Name} ({h.Purpose})"));
    }

    // ---------------- Small utilities ----------------

    public static Version ParseMin(string min)
    {
        var parts = min.Split('.');
        return new Version(int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 0, parts.Length > 2 ? int.Parse(parts[2]) : 0);
    }

    public static string? FindOnPath(string file) => FindAllOnPath(file).FirstOrDefault();

    public static IEnumerable<string> FindAllOnPath(string file)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.Combine(dir.Trim().Trim('"'), file); }
            catch (ArgumentException) { continue; }
            if (File.Exists(full)) yield return full;
        }
    }

    /// <summary>Run a short command, return stdout, or null on any failure or a 10 s timeout.</summary>
    public static string? RunCapture(string file, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch (Exception) { } return null; }
            return p.ExitCode == 0 ? outTask.Result : null;
        }
        catch (Exception) { return null; }
    }
}
