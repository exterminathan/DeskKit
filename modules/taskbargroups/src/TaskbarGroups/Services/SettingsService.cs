using System.Text.Json;
using TaskbarGroups.Models;

namespace TaskbarGroups.Services;

/// <summary>settings.json in the data dir (docs/module-contract.md section 6).</summary>
public static class SettingsService
{
    private static string SettingsPath => ConfigService.SettingsPath;

    private static readonly string[] Keys = { "shortcutsPath", "stayOpenUntilClickOff" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static AppSettings? _cached;

    public static AppSettings Current => _cached ??= Load();

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        File.WriteAllText(SettingsPath, json);
        _cached = settings;
    }

    public static void Reload()
    {
        _cached = null;
    }

    /// <summary>Writes settings.json with every key when it is missing or lacks a key.</summary>
    public static void EnsureFile()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    Keys.All(k => doc.RootElement.TryGetProperty(k, out _)))
                    return;
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return; // not ours to overwrite; Load() falls back to defaults
            }
            Save(Current);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not write settings.json: {ex.Message}");
        }
    }

    private static AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load settings.json: {ex.Message}");
            return new AppSettings();
        }
    }
}
