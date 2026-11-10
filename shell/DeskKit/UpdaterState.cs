// The "updates" object in %APPDATA%\DeskKit\shell\state.json (contract 16.8). Read-modify-write that keeps
// every other top-level key; ShellState.Save does the same for "modules". Both take StateFileLock.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskKit.Shell;

public static class UpdaterState
{
    public static readonly object StateFileLock = new();

    private static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Read state.json as an object; a missing or broken file gives an empty object.</summary>
    public static JsonObject ReadRoot(string file)
    {
        try
        {
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject o) return o;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            ShellLog.Write($"state.json unreadable: {e.Message}");
        }
        return new JsonObject();
    }

    public static void WriteRoot(string file, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Write) + Environment.NewLine);
        File.Move(tmp, file, overwrite: true);
    }

    private static JsonObject Updates(JsonObject root)
    {
        if (root["updates"] is JsonObject u) return u;
        u = new JsonObject();
        root["updates"] = u;
        return u;
    }

    private static T? Get<T>(string key)
    {
        lock (StateFileLock)
        {
            var root = ReadRoot(ShellPaths.StateFile);
            if (root["updates"] is JsonObject u && u[key] is JsonValue v && v.TryGetValue<T>(out var t)) return t;
            return default;
        }
    }

    private static void Edit(Action<JsonObject> change)
    {
        lock (StateFileLock)
        {
            try
            {
                var root = ReadRoot(ShellPaths.StateFile);
                change(Updates(root));
                WriteRoot(ShellPaths.StateFile, root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ShellLog.Write($"state.json: cannot write updates: {e.Message}");
            }
        }
    }

    /// <summary>Automatic checks (default on).</summary>
    public static bool CheckEnabled
    {
        get => Get<bool?>("check_enabled") ?? true;
        set => Edit(u => u["check_enabled"] = value);
    }

    /// <summary>Include versions with a SemVer "-" part (default off, contract 16.2).</summary>
    public static bool IncludePrereleases
    {
        get => Get<bool?>("include_prereleases") ?? false;
        set => Edit(u => u["include_prereleases"] = value);
    }

    public static DateTimeOffset? LastCheck
    {
        get => DateTimeOffset.TryParse(Get<string>("last_check"), out var t) ? t : null;
        set => Edit(u => u["last_check"] = value?.ToString("yyyy-MM-ddTHH:mm:sszzz"));
    }

    public static string? Notified(string id)
    {
        lock (StateFileLock)
        {
            var root = ReadRoot(ShellPaths.StateFile);
            return root["updates"]?["notified"]?[id] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
    }

    public static void SetNotified(string id, string version) => Edit(u =>
    {
        if (u["notified"] is not JsonObject n) { n = new JsonObject(); u["notified"] = n; }
        n[id] = version;
    });

    public static void RecordResult(string id, string? from, string? to, string result, string message)
    {
        ShellLog.Write($"update {id}: {result} {from} -> {to}{(message.Length > 0 ? ": " + message : "")}");
        Edit(u =>
        {
            if (u["results"] is not JsonObject r) { r = new JsonObject(); u["results"] = r; }
            r[id] = new JsonObject
            {
                ["from"] = from, ["to"] = to, ["result"] = result,
                ["time"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"), ["message"] = message,
            };
        });
    }

    /// <summary>The last recorded result of a module: result word and message, or null.</summary>
    public static (string Result, string To, string Message)? LastResult(string id)
    {
        lock (StateFileLock)
        {
            var root = ReadRoot(ShellPaths.StateFile);
            if (root["updates"]?["results"]?[id] is not JsonObject r) return null;
            return ((string?)r["result"] ?? "", (string?)r["to"] ?? "", (string?)r["message"] ?? "");
        }
    }
}
