// module.json model and discovery (contract 3 and 7.4). The validator checks the schema strictly;
// the shell only needs to read the keys it uses, and shows a file that fails to parse as "broken".
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DeskKit.Shell;

public sealed class RuntimeInfo
{
    public string Type { get; set; } = "";
    public string Min { get; set; } = "";
    public bool? SelfContained { get; set; }
    public string? Requirements { get; set; }
}

public sealed class InstanceInfo
{
    public bool Single { get; set; }
    public string? Mutex { get; set; }
    public string? ShowEvent { get; set; }
    public string? StopEvent { get; set; }
}

public sealed class ActionInfo
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> Args { get; set; } = new();
}

public sealed class AutostartInfo
{
    public bool Default { get; set; }
    public string ManagedBy { get; set; } = "shell";
}

public sealed class HelperDetect
{
    public string? Setting { get; set; }
    public List<string> Paths { get; set; } = new();
    public string? OnPath { get; set; }
}

public sealed class HelperDownload
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Archive { get; set; } = "none";
    public string Page { get; set; } = "";
}

public sealed class HelperInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Required { get; set; }
    public string Purpose { get; set; } = "";
    public HelperDetect Detect { get; set; } = new();
    public HelperDownload Download { get; set; } = new();
    public string Licence { get; set; } = "";
}

public sealed class ModuleManifest
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Repo { get; set; }
    public RuntimeInfo Runtime { get; set; } = new();
    public string Entry { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public bool Console { get; set; }
    public string RunMode { get; set; } = "";
    public InstanceInfo Instance { get; set; } = new();
    public List<ActionInfo> Actions { get; set; } = new();
    public AutostartInfo Autostart { get; set; } = new();
    public string? Icon { get; set; }
    public string DataDir { get; set; } = "";
    public string? SettingsPath { get; set; }
    public string? SettingsAction { get; set; }
    public string? Launcher { get; set; }
    public List<HelperInfo> Helpers { get; set; } = new();
    public string Licence { get; set; } = "";
    public string? Homepage { get; set; }
    public string? Notes { get; set; }
}

/// <summary>One folder found under modules\. Either Manifest is set, or Error says why it is broken.</summary>
public sealed class ModuleEntry
{
    public required string Folder { get; init; }       // module dir, absolute, no trailing backslash
    public required string FolderName { get; init; }
    public ModuleManifest? Manifest { get; init; }
    public string? Error { get; init; }

    public bool IsBroken => Manifest is null;
    public string Id => Manifest?.Id ?? FolderName;
    public string DisplayName => Manifest?.Name ?? FolderName;
}

public static class ModuleScanner
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private static readonly string[] RunModes = { "tray", "window", "popup", "oneshot" };
    private static readonly string[] RuntimeTypes = { "dotnet", "python", "powershell", "native" };

    /// <summary>Read every modules\*\module.json, skipping folders that start with "_" (contract 2, 7.4).</summary>
    public static List<ModuleEntry> Scan(string modulesDir)
    {
        var list = new List<ModuleEntry>();
        if (!Directory.Exists(modulesDir)) return list;
        var planned = PlannedIds(modulesDir);
        foreach (var dir in Directory.GetDirectories(modulesDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('_') || planned.Contains(name)) continue;
            var file = Path.Combine(dir, "module.json");
            if (!File.Exists(file)) continue;
            list.Add(Load(file));
        }
        return list;
    }

    /// <summary>Ids that suite.json (next to modules\) marks "status": "planned": scaffolds the shell must not list (contract 11).</summary>
    public static HashSet<string> PlannedIds(string modulesDir)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suite = UpdateSources.ReadSuite(modulesDir);
        if (suite?["modules"] is JsonArray a)
            foreach (var m in a.OfType<JsonObject>())
                if ((string?)m["status"] == "planned" && (string?)m["id"] is { } id) ids.Add(id);
        return ids;
    }

    public static ModuleEntry Load(string file)
    {
        var dir = Path.GetFullPath(Path.GetDirectoryName(file)!).TrimEnd('\\');
        var name = Path.GetFileName(dir);
        try
        {
            var m = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(file), Options)
                    ?? throw new JsonException("module.json is empty.");
            var problem = Check(m, name);
            if (problem != null) return new ModuleEntry { Folder = dir, FolderName = name, Error = problem };
            return new ModuleEntry { Folder = dir, FolderName = name, Manifest = m };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ModuleEntry { Folder = dir, FolderName = name, Error = e.Message };
        }
    }

    /// <summary>The few checks the shell needs to run a module safely. The validator does the rest.</summary>
    private static string? Check(ModuleManifest m, string folderName)
    {
        if (m.SchemaVersion != 1) return $"schema_version {m.SchemaVersion} is not supported (expected 1).";
        if (string.IsNullOrWhiteSpace(m.Id)) return "id is missing.";
        if (!string.Equals(m.Id, folderName.TrimStart('_'), StringComparison.Ordinal))
            return $"id \"{m.Id}\" does not match the folder name \"{folderName}\".";
        if (string.IsNullOrWhiteSpace(m.Name)) return "name is missing.";
        if (!RunModes.Contains(m.RunMode)) return $"run_mode \"{m.RunMode}\" is not tray, window, popup or oneshot.";
        if (!RuntimeTypes.Contains(m.Runtime.Type)) return $"runtime.type \"{m.Runtime.Type}\" is not supported.";
        if (string.IsNullOrWhiteSpace(m.Entry) || m.Entry.Contains("..") || Path.IsPathRooted(m.Entry) || m.Entry.Contains(':'))
            return $"entry \"{m.Entry}\" must be a relative path inside the module dir.";
        if (string.IsNullOrWhiteSpace(m.DataDir)) return "data_dir is missing.";
        var ids = new HashSet<string>();
        foreach (var a in m.Actions)
            if (string.IsNullOrWhiteSpace(a.Id) || !ids.Add(a.Id)) return $"action id \"{a.Id}\" is empty or repeated.";
        if (m.SettingsAction != null && !ids.Contains(m.SettingsAction))
            return $"settings_action \"{m.SettingsAction}\" names no action.";
        return null;
    }
}
