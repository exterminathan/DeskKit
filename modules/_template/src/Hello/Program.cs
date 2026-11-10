// DeskKit template module (C#, .NET 8): print a greeting and exit.
// Copy modules/_template/ to modules/<id>/ to start a module. It shows the contract basics
// (docs/module-contract.md): data dir and settings.json with defaults (6), exit codes and
// one ERROR line on stderr (9). Usage: Hello.exe [name]
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskKit.Modules.Hello;

internal static class ExitCode
{
    public const int Ok = 0, Error = 1, Usage = 2, HelperMissing = 3, RuntimeMissing = 4,
        DeviceMissing = 5, PackagesFailed = 6;
}

internal static class Program
{
    private const string ModuleId = "template";
    private const string Name = "Hello";

    private static int Main(string[] args)
    {
        // A tray or window module starts like this (contract 7.2):
        //   using var instance = SingleInstance.TryStart(ModuleId, onShow: ..., onStop: ...);
        //   if (instance is null) return ExitCode.Ok;   // another copy runs; it was told to show itself
        if (args.Length > 1 || args.Any(a => a.StartsWith('-')))
            return Fail(ExitCode.Usage, "Usage: Hello.exe [name]");

        JsonObject settings;
        try { settings = LoadSettings(); }
        catch (Exception e) { return Fail(ExitCode.Error, $"Cannot read settings: {e.Message}"); }

        var who = args.Length == 1 ? args[0] : "DeskKit";
        Console.WriteLine($"{settings["greeting"]}, {who}! ({Name}, .NET {Environment.Version})");
        return ExitCode.Ok;
    }

    /// <summary>%APPDATA%\DeskKit\&lt;id&gt;, or what the shell passed in DESKKIT_DATA_DIR.</summary>
    private static string DataDir() =>
        Environment.GetEnvironmentVariable("DESKKIT_DATA_DIR") is { Length: > 0 } passed
            ? passed
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskKit", ModuleId);

    /// <summary>Read settings.json; write it with all defaults when it is missing (contract 6).</summary>
    private static JsonObject LoadSettings()
    {
        var defaults = new JsonObject { ["greeting"] = "Hello" };
        var path = Path.Combine(DataDir(), "settings.json");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, defaults.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return defaults;
        }
        var read = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidDataException($"{path} is not a JSON object.");
        foreach (var (key, value) in defaults)
            if (!read.ContainsKey(key)) read[key] = value?.DeepClone();
        return read;
    }

    /// <summary>Write one ERROR line to stderr and return the exit code (contract 9.2).
    /// A module with "console": false also shows a message box when DESKKIT_SHELL is not set.</summary>
    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        return code;
    }
}
