using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskbarGroups.Services;

/// <summary>
/// First-run setup of the data dir. Copies (never moves) the config of an older
/// LightweightTaskbarGroups install, so existing groups and pinned shortcuts keep working,
/// then writes the default icons and settings.json.
/// </summary>
public static class MigrationService
{
    /// <summary>Overrides the old install folder to copy from (used by tests).</summary>
    public const string LegacyDirVariable = "TASKBARGROUPS_LEGACY_DIR";

    public const string MarkerFile = "migrated-from.txt";

    public static void Run()
    {
        var hadData = Directory.Exists(ConfigService.ConfigRoot) || File.Exists(ConfigService.SettingsPath);
        if (!hadData)
        {
            try { CopyFromLegacy(); }
            catch (Exception ex) { Console.Error.WriteLine($"Copying the old config failed: {ex.Message}"); }
        }

        try { WriteDefaultIcons(); }
        catch (Exception ex) { Console.Error.WriteLine($"Writing default icons failed: {ex.Message}"); }

        SettingsService.EnsureFile();
    }

    /// <summary>
    /// Old install folders, in order: the exe folder (the old app kept config\ next to the exe),
    /// then TASKBARGROUPS_LEGACY_DIR if set, else &lt;system drive&gt;\TaskbarGroups (the old default).
    /// </summary>
    public static IEnumerable<string> LegacyRoots()
    {
        yield return ConfigService.ExeDir;
        var overridden = Environment.GetEnvironmentVariable(LegacyDirVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            yield return overridden;
        }
        else
        {
            var drive = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrEmpty(drive)) yield return Path.Combine(drive, "TaskbarGroups");
        }
    }

    private static string? CopyFromLegacy()
    {
        var dataFull = Path.GetFullPath(ConfigService.DataRoot).TrimEnd('\\');
        foreach (var root in LegacyRoots())
        {
            string rootFull;
            try { rootFull = Path.GetFullPath(root).TrimEnd('\\'); }
            catch { continue; }
            if (string.Equals(rootFull, dataFull, StringComparison.OrdinalIgnoreCase)) continue;

            var oldConfig = Path.Combine(rootFull, "config");
            var oldSettings = Path.Combine(rootFull, "settings.json");
            var oldIcons = Path.Combine(rootFull, "icons");
            if (!Directory.Exists(oldConfig) && !File.Exists(oldSettings)) continue;

            Directory.CreateDirectory(ConfigService.DataRoot);
            if (Directory.Exists(oldConfig)) CopyTree(oldConfig, ConfigService.ConfigRoot);
            if (Directory.Exists(oldIcons)) CopyTree(oldIcons, ConfigService.IconsRoot);
            if (File.Exists(oldSettings)) File.Copy(oldSettings, ConfigService.SettingsPath, overwrite: false);
            try { RelocateGroupIcons(); }
            catch (Exception ex) { Console.Error.WriteLine($"Copying group icons failed: {ex.Message}"); }

            File.WriteAllText(Path.Combine(ConfigService.DataRoot, MarkerFile),
                $"Copied from {rootFull} on {DateTime.Now:yyyy-MM-dd HH:mm:ss}. The old folder was not changed.{Environment.NewLine}");
            SettingsService.Reload();
            return rootFull;
        }
        return null;
    }

    /// <summary>
    /// For each copied group.json whose iconPath is an absolute path inside an old install folder,
    /// copies that icon into &lt;data dir&gt;\icons and rewrites iconPath to the copy (relative, "icons/name").
    /// The old folder is only read.
    /// </summary>
    internal static void RelocateGroupIcons()
    {
        if (!Directory.Exists(ConfigService.ConfigRoot)) return;
        var roots = new List<string>();
        foreach (var r in LegacyRoots())
        {
            try { roots.Add(Path.GetFullPath(r).TrimEnd('\\') + "\\"); } catch { }
        }
        foreach (var json in Directory.EnumerateFiles(ConfigService.ConfigRoot, "group.json", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(json);
                if (JsonNode.Parse(text) is not JsonObject obj) continue;
                var stored = obj["iconPath"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(stored)) continue;
                var expanded = Environment.ExpandEnvironmentVariables(stored.Trim());
                if (!Path.IsPathRooted(expanded)) continue;
                var full = Path.GetFullPath(expanded);
                if (!roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase))) continue;
                if (!File.Exists(full)) continue;

                Directory.CreateDirectory(ConfigService.IconsRoot);
                var name = Path.GetFileName(full);
                var dest = Path.Combine(ConfigService.IconsRoot, name);
                for (var i = 2; File.Exists(dest) && !SameContent(full, dest); i++)
                    dest = Path.Combine(ConfigService.IconsRoot,
                        Path.GetFileNameWithoutExtension(name) + "-" + i + Path.GetExtension(name));
                if (!File.Exists(dest)) File.Copy(full, dest);

                obj["iconPath"] = "icons/" + Path.GetFileName(dest);
                File.WriteAllText(json, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not relocate the icon of '{json}': {ex.Message}");
            }
        }
    }

    private static bool SameContent(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            if (!File.Exists(dest)) File.Copy(file, dest);
        }
    }

    /// <summary>Writes the bundled icons (link.ico, jarv.ico) to &lt;data dir&gt;\icons when missing.</summary>
    private static void WriteDefaultIcons()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith("icons.", StringComparison.Ordinal)) continue;
            var dest = Path.Combine(ConfigService.IconsRoot, res.Substring("icons.".Length));
            if (File.Exists(dest)) continue;
            Directory.CreateDirectory(ConfigService.IconsRoot);
            using var src = asm.GetManifestResourceStream(res);
            if (src is null) continue;
            using var fs = File.Create(dest);
            src.CopyTo(fs);
        }
    }
}
