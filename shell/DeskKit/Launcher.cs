// Start one module (contract 5) with the DESKKIT_* variables and its output in the shell's log (9.3).
using System.Collections;
using System.Diagnostics;
using System.Text;

namespace DeskKit.Shell;

public static class Launcher
{
    public const long MaxLogBytes = 1024 * 1024;

    public static string LogFile(string id) => Path.Combine(ShellPaths.ModuleLogsDir, id + ".log");

    /// <summary>
    /// Start the module with these raw (unexpanded) args, then the extra args from "--run id -- ..." unchanged.
    /// Throws RuntimeMissingException when the runtime is missing, or FileNotFoundException when the entry is missing.
    /// </summary>
    public static Process Start(ModuleEntry e, IReadOnlyList<string> rawArgs, IReadOnlyList<string>? extraArgs = null)
    {
        var m = e.Manifest ?? throw new InvalidOperationException($"{e.FolderName}: module.json is broken: {e.Error}");
        var entry = Runtimes.EntryPath(e);
        if (!File.Exists(entry))
            throw new FileNotFoundException($"{m.Name} cannot start: {m.Entry} is missing.", entry);
        var cmd = Runtimes.BuildCommand(e, rawArgs, extraArgs);
        var dataDir = Runtimes.ExpandDataDir(e);

        var env = EnvironmentBlock(new Dictionary<string, string>
        {
            ["DESKKIT_SHELL"] = "1",
            ["DESKKIT_MODULE_ID"] = m.Id,
            ["DESKKIT_MODULE_DIR"] = e.Folder,
            ["DESKKIT_DATA_DIR"] = dataDir,
        });

        var log = LogFile(m.Id);
        WriteStartLine(log, extraArgs == null ? rawArgs : rawArgs.Concat(extraArgs));
        ShellLog.Write($"start {m.Id}: {cmd.CommandLine}");

        // console:true modules keep their visible console and are not redirected (contract 9.3 exception).
        if (cmd.NewConsole)
            return Native.StartProcess(cmd.CommandLine, cmd.WorkingDir, env, newConsole: true, output: null);
        using var handle = Native.OpenAppendInheritable(log);
        return Native.StartProcess(cmd.CommandLine, cmd.WorkingDir, env, newConsole: false, output: handle);
    }

    /// <summary>Rotate at 1 MB, then append "=== ISO time start args ===".</summary>
    public static void WriteStartLine(string log, IEnumerable<string> rawArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        try
        {
            var info = new FileInfo(log);
            if (info.Exists && info.Length > MaxLogBytes) File.Move(log, log + ".1", overwrite: true);
        }
        catch (IOException) { /* another copy holds the file open: keep appending */ }
        catch (UnauthorizedAccessException) { }
        var line = $"=== {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz} start {string.Join(' ', rawArgs)} ==={Environment.NewLine}";
        using var fs = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = Encoding.UTF8.GetBytes(line);
        fs.Write(bytes);
    }

    /// <summary>The last "ERROR:" line after the last start marker, else the last non-empty line, or null.</summary>
    public static string? LastErrorLine(string id)
    {
        var log = LogFile(id);
        if (!File.Exists(log)) return null;
        string[] lines;
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 64 * 1024) fs.Seek(-64 * 1024, SeekOrigin.End);
            using var r = new StreamReader(fs);
            lines = r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (IOException) { return null; }
        int start = Array.FindLastIndex(lines, l => l.StartsWith("=== ") && l.Contains(" start "));
        var tail = lines.Skip(start + 1).Where(l => l.Trim().Length > 0).ToList();
        return tail.LastOrDefault(l => l.StartsWith("ERROR:")) ?? tail.LastOrDefault();
    }

    /// <summary>This process's environment plus extras, as a sorted Unicode block for CreateProcess.</summary>
    public static string EnvironmentBlock(Dictionary<string, string> extra)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
            vars[(string)kv.Key] = (string?)kv.Value ?? "";
        foreach (var (k, v) in extra) vars[k] = v;
        var sb = new StringBuilder();
        foreach (var (k, v) in vars)
        {
            if (k.Length == 0 || k.Contains('=') && k[0] != '=') continue;
            sb.Append(k).Append('=').Append(v).Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }
}

/// <summary>The shell's own log, %APPDATA%\DeskKit\shell\shell.log. Never throws.</summary>
public static class ShellLog
{
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                var file = ShellPaths.ShellLogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var info = new FileInfo(file);
                if (info.Exists && info.Length > Launcher.MaxLogBytes) File.Move(file, file + ".1", overwrite: true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* logging must never break the shell */ }
    }
}
