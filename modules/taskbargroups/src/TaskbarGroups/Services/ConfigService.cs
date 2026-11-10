using System.Text.Json;
using TaskbarGroups.Models;

namespace TaskbarGroups.Services;

public static class ConfigService
{
    public const string ModuleId = "taskbargroups";

    /// <summary>Folder of the running exe. Read-only to this app: nothing is written here.</summary>
    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>
    /// Data dir (docs/module-contract.md section 6): DESKKIT_DATA_DIR when the shell passes it,
    /// else %APPDATA%\DeskKit\taskbargroups. %APPDATA% is read from the environment first, so a
    /// test can point it at a throwaway folder.
    /// </summary>
    public static string DataRoot { get; } = ResolveDataRoot();

    public static string ConfigRoot => Path.Combine(DataRoot, "config");
    public static string IconsRoot => Path.Combine(DataRoot, "icons");
    public static string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public static string DefaultShortcutsRoot => Path.Combine(DataRoot, "Shortcuts");

    public static string ShortcutsRoot
    {
        get
        {
            var custom = SettingsService.Current.ShortcutsPath;
            return string.IsNullOrWhiteSpace(custom) ? DefaultShortcutsRoot : custom;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string ResolveDataRoot()
    {
        var passed = Environment.GetEnvironmentVariable("DESKKIT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(passed)) return Path.GetFullPath(passed);

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "DeskKit", ModuleId);
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigRoot);
        try
        {
            Directory.CreateDirectory(ShortcutsRoot);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not create shortcuts folder '{ShortcutsRoot}': {ex.Message}");
            Directory.CreateDirectory(DefaultShortcutsRoot);
        }
    }

    /// <summary>
    /// The group icon path to use on disk. A relative path is resolved against the data dir.
    /// An absolute path is kept as stored; if that file is gone (an old install folder was
    /// deleted), the same file name in &lt;data dir&gt;\icons is used instead when it exists.
    /// The stored value is never rewritten.
    /// </summary>
    public static string ResolveIconPath(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        var expanded = Environment.ExpandEnvironmentVariables(stored.Trim());
        try
        {
            if (!Path.IsPathRooted(expanded))
                return Path.GetFullPath(Path.Combine(DataRoot, expanded));
            if (File.Exists(expanded)) return expanded;
            var fallback = Path.Combine(IconsRoot, Path.GetFileName(expanded));
            return File.Exists(fallback) ? fallback : expanded;
        }
        catch
        {
            return expanded;
        }
    }

    /// <summary>
    /// The value to store for an icon the user picked: relative to the data dir when the file
    /// lies inside it (so the data dir can move), else the absolute path.
    /// </summary>
    public static string ToStoredIconPath(string picked)
    {
        try
        {
            var full = Path.GetFullPath(picked);
            var root = Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(DataRoot, full);
            return full;
        }
        catch
        {
            return picked;
        }
    }

    public static IReadOnlyList<GroupConfig> GetAllGroups()
    {
        var result = new List<GroupConfig>();
        if (!Directory.Exists(ConfigRoot)) return result;

        foreach (var dir in Directory.EnumerateDirectories(ConfigRoot))
        {
            var name = Path.GetFileName(dir);
            try
            {
                var g = LoadGroup(name);
                if (g is not null) result.Add(g);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load group '{name}': {ex.Message}");
            }
        }
        return result;
    }

    public static GroupConfig? LoadGroup(string name)
    {
        var path = Path.Combine(ConfigRoot, name, "group.json");
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GroupConfig>(json, JsonOpts);
    }

    /// <summary>LoadGroup without throwing: null plus a message when the file is unreadable.</summary>
    public static GroupConfig? LoadGroupSafe(string name, out string? error)
    {
        error = null;
        try
        {
            return LoadGroup(name);
        }
        catch (Exception ex)
        {
            error = $"Cannot read group '{name}' from {Path.Combine(ConfigRoot, name, "group.json")}: {ex.Message}";
            return null;
        }
    }

    public static void SaveGroup(GroupConfig group)
    {
        if (string.IsNullOrWhiteSpace(group.Name))
            throw new ArgumentException("Group name is required.", nameof(group));

        var groupDir = Path.Combine(ConfigRoot, group.Name);
        var iconsDir = Path.Combine(groupDir, "icons");
        Directory.CreateDirectory(iconsDir);

        var json = JsonSerializer.Serialize(group, JsonOpts);
        File.WriteAllText(Path.Combine(groupDir, "group.json"), json);
    }

    public static void DeleteGroup(string name)
    {
        var groupDir = Path.Combine(ConfigRoot, name);
        if (Directory.Exists(groupDir))
        {
            Directory.Delete(groupDir, recursive: true);
        }

        var lnkPath = Path.Combine(ShortcutsRoot, name + ".lnk");
        if (File.Exists(lnkPath))
        {
            File.Delete(lnkPath);
        }
    }

    public static string SanitizeFileName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Where(c => !invalid.Contains(c)).ToArray();
        var clean = new string(chars).Trim();
        return string.IsNullOrEmpty(clean) ? "_" : clean;
    }

    public static bool IsValidGroupName(string name, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Group name cannot be empty.";
            return false;
        }
        if (name.Length > 49)
        {
            error = "Group name must be 49 characters or fewer.";
            return false;
        }
        var invalid = Path.GetInvalidFileNameChars();
        if (name.Any(c => invalid.Contains(c)))
        {
            error = "Group name contains invalid characters.";
            return false;
        }
        error = null;
        return true;
    }
}
