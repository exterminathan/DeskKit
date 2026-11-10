// Autostart (contract 6 and 7.4): per-module on/off in %APPDATA%\DeskKit\shell\state.json, and one
// HKCU Run value "DeskKit" for the shell itself. The shell never writes a module's own login entry.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DeskKit.Shell;

public sealed class ShellState
{
    private readonly string _file;
    private readonly Dictionary<string, bool> _autostart = new(StringComparer.Ordinal);

    public ShellState(string file)
    {
        _file = file;
        Load();
    }

    public static ShellState Open() => new(ShellPaths.StateFile);

    private void Load()
    {
        _autostart.Clear();
        if (!File.Exists(_file)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(_file)) is JsonObject root && root["modules"] is JsonObject mods)
                foreach (var (id, node) in mods)
                    if (node is JsonObject o && o["autostart"] is JsonValue v && v.TryGetValue<bool>(out var on))
                        _autostart[id] = on;
        }
        catch (Exception e)
        {
            ShellLog.Write($"state.json unreadable, using defaults: {e.Message}");
        }
    }

    /// <summary>The module's autostart: state.json when it has an entry, else autostart.default.</summary>
    public bool IsAutostartOn(ModuleManifest m) =>
        _autostart.TryGetValue(m.Id, out var on) ? on : m.Autostart.Default;

    public bool HasEntry(string id) => _autostart.ContainsKey(id);

    public void SetAutostart(string id, bool on)
    {
        _autostart[id] = on;
        Save();
    }

    private void Save()
    {
        var mods = new JsonObject();
        foreach (var (id, on) in _autostart.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            mods[id] = new JsonObject { ["autostart"] = on };
        lock (UpdaterState.StateFileLock)   // keep other top-level keys, such as "updates" (contract 16.8)
        {
            var root = UpdaterState.ReadRoot(_file);
            root["modules"] = mods;
            UpdaterState.WriteRoot(_file, root);
        }
    }
}

/// <summary>HKCU Run access. DESKKIT_RUN_KEY (a subkey of HKCU) replaces the real Run key in tests.</summary>
public static class LoginEntry
{
    public const string ShellValueName = "DeskKit";
    private const string RealRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string RunKeyPath =>
        Environment.GetEnvironmentVariable("DESKKIT_RUN_KEY") is { Length: > 0 } k ? k : RealRunKey;

    public static string ShellCommand(string exePath, string? modulesDirArg)
    {
        var cmd = CommandLineBuilder.Build(exePath, Array.Empty<string>());
        if (!cmd.StartsWith('"')) cmd = $"\"{exePath}\"";
        if (modulesDirArg != null) cmd += " --modules-dir " + CommandLineBuilder.Build(modulesDirArg, Array.Empty<string>());
        return cmd;
    }

    public static bool IsShellOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ShellValueName) is string s && s.Length > 0;
    }

    public static void SetShell(bool on, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (on) key.SetValue(ShellValueName, command, RegistryValueKind.String);
        else if (key.GetValue(ShellValueName) != null) key.DeleteValue(ShellValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// For managed_by "module": read-only check whether some Run value starts this module's exe.
    /// Returns null when the shell cannot tell (no exe name to match).
    /// </summary>
    public static bool? ModuleOwnEntryOn(ModuleEntry e)
    {
        var exe = Path.GetFileName(e.Manifest!.Entry.Replace('/', '\\'));
        if (string.IsNullOrEmpty(exe)) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key == null) return false;
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name) is string s && s.Contains(exe, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch (Exception) { return null; }
    }
}
