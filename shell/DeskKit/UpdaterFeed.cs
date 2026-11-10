// Update sources and the GitHub Releases feed (contract 16.1, 16.2, 16.7): which repo, tag and asset each
// component uses, one cached GET per repo (ETag), and the choice of the newest release.
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DeskKit.Shell;

public enum ComponentKind { InRepo, Pinned, Shell, Separate }

public enum UpdateStateKind { Current, Update, NotifyOnly, NoSource, Error }

public sealed record ReleaseAsset(string Name, string Url, string? Digest);

public sealed record FeedRelease(string Tag, bool Draft, string HtmlUrl, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>One thing the shell can check: a module, the shell, or a separately installed Region Control.</summary>
public sealed class UpdateComponent
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ComponentKind Kind { get; init; }
    public required string InstalledVersion { get; init; }
    public string? Repo { get; init; }                    // https://github.com/<owner>/<repo>
    public string TagPrefix { get; init; } = "v";
    public Func<string, string>? AssetName { get; init; } // version -> asset file name
    public ModuleEntry? Entry { get; init; }
    public string? NotifyOnlyReason { get; init; }        // set: never written to (contract 16.5)
    public string? InstallLocation { get; init; }
}

public sealed class UpdateCheck
{
    public required UpdateComponent Component { get; init; }
    public UpdateStateKind State { get; set; }
    public string? Available { get; set; }
    public FeedRelease? Release { get; set; }
    public ReleaseAsset? Asset { get; set; }
    public string Note { get; set; } = "";

    public bool HasUpdate => State is UpdateStateKind.Update or UpdateStateKind.NotifyOnly && Available != null;
}

public sealed class FeedException(string message) : Exception(message);

public static class UpdateSources
{
    /// <summary>Region Control's own installer AppId (contract 10.4).</summary>
    public const string RegionControlUninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F1D2B7E-3C54-4E0A-9B6A-5D2C8A41E7F3}_is1";

    public static string FeedBase =>
        (Environment.GetEnvironmentVariable("DESKKIT_FEED_BASE") is { Length: > 0 } b ? b : "https://api.github.com").TrimEnd('/');

    public static bool TestFeed => Environment.GetEnvironmentVariable("DESKKIT_FEED_BASE") is { Length: > 0 };

    /// <summary>HTTPS always; plain http only to loopback while DESKKIT_FEED_BASE is set (contract 16.10).</summary>
    public static bool UrlAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme == Uri.UriSchemeHttps) return true;
        return TestFeed && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;
    }

    public static string SuiteFile(string modulesDir) =>
        Path.Combine(Path.GetDirectoryName(modulesDir.TrimEnd('\\')) ?? modulesDir, "suite.json");

    public static JsonObject? ReadSuite(string modulesDir)
    {
        try
        {
            var f = SuiteFile(modulesDir);
            return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            ShellLog.Write($"updates: suite.json unreadable: {e.Message}");
            return null;
        }
    }

    private static JsonObject? SuiteEntry(JsonObject? suite, string id) =>
        suite?["modules"] is JsonArray a ? a.OfType<JsonObject>().FirstOrDefault(m => (string?)m["id"] == id) : null;

    /// <summary>The asset rule of a pinned module: asset_pattern, else the url's file name with the version replaced.</summary>
    public static Func<string, string>? PinnedAssetRule(JsonObject? entry)
    {
        if (entry == null) return null;
        if ((string?)entry["asset_pattern"] is { Length: > 0 } pattern) return v => pattern.Replace("{version}", v);
        var url = (string?)entry["url"]; var pinned = (string?)entry["version"];
        if (url == null || pinned == null) return null;
        var name = Path.GetFileName(new Uri(url).LocalPath);
        if (!name.Contains(pinned)) return null;
        return v => name.Replace(pinned, v);
    }

    /// <summary>Create and delete &lt;modules dir&gt;\_update\.write-test; remove _update again if this made it.</summary>
    public static bool IsWritable(string modulesDir)
    {
        var dir = Path.Combine(modulesDir, "_update");
        bool made = !Directory.Exists(dir);
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            if (made) try { Directory.Delete(dir); } catch (Exception) { }
        }
    }

    /// <summary>The DeskKit repo itself (development with --modules-dir): its parent holds schemas\module.schema.json.</summary>
    public static bool IsSourceTree(string modulesDir) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(modulesDir.TrimEnd('\\')) ?? modulesDir, "schemas", "module.schema.json"));

    /// <summary>Every component to check: each scanned module, the shell, and a separate Region Control.</summary>
    public static List<UpdateComponent> Components(string modulesDir, IEnumerable<ModuleEntry> modules)
    {
        var suite = ReadSuite(modulesDir);
        var suiteRepo = (string?)suite?["repo"];
        bool? writable = null;
        bool sourceTree = IsSourceTree(modulesDir);
        var list = new List<UpdateComponent>();
        foreach (var e in modules.Where(m => !m.IsBroken))
        {
            var m = e.Manifest!;
            string? reason = null;
            if (sourceTree || File.Exists(Path.Combine(e.Folder, "build.ps1")))
                reason = "This is a source folder (development); DeskKit never writes into it.";
            else if ((writable ??= IsWritable(modulesDir)) == false)
                reason = $"DeskKit cannot write to {modulesDir}.";
            if (m.Source == "pinned")
                list.Add(new UpdateComponent
                {
                    Id = m.Id, Name = m.Name, Kind = ComponentKind.Pinned, InstalledVersion = m.Version, Repo = m.Repo,
                    TagPrefix = "v", AssetName = PinnedAssetRule(SuiteEntry(suite, m.Id)), Entry = e, NotifyOnlyReason = reason,
                });
            else
                list.Add(new UpdateComponent
                {
                    Id = m.Id, Name = m.Name, Kind = ComponentKind.InRepo, InstalledVersion = m.Version, Repo = suiteRepo,
                    TagPrefix = m.Id + "/v", AssetName = v => $"{m.Id}-{v}.zip", Entry = e, NotifyOnlyReason = reason,
                });
        }

        list.Add(new UpdateComponent
        {
            Id = "shell", Name = "DeskKit", Kind = ComponentKind.Shell, InstalledVersion = Program.Version, Repo = suiteRepo,
            TagPrefix = "v", AssetName = v => $"DeskKit-Setup-{v}.exe",
        });

        if (!list.Any(c => c.Id == "regioncontrol") && SeparateRegionControl() is { } rc)
        {
            var entry = SuiteEntry(suite, "regioncontrol");
            list.Add(new UpdateComponent
            {
                Id = "regioncontrol", Name = "Snap Layouts", Kind = ComponentKind.Separate, InstalledVersion = rc.Version,
                Repo = (string?)entry?["repo"], TagPrefix = "v", AssetName = PinnedAssetRule(entry), InstallLocation = rc.Location,
                NotifyOnlyReason = $"Snap Layouts is installed separately{(rc.Location != null ? " in " + rc.Location : "")}. Update it from its release page.",
            });
        }
        return list;
    }

    /// <summary>Region Control's own install, read from its uninstall key (HKCU, then HKLM 64/32), or DESKKIT_RC_UNINSTALL_KEY.</summary>
    public static (string Version, string? Location)? SeparateRegionControl()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("DESKKIT_RC_UNINSTALL_KEY") is { Length: > 0 } test)
            {
                using var k = Registry.CurrentUser.OpenSubKey(test);
                return Read(k);
            }
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var b = RegistryKey.OpenBaseKey(hive, view);
                    using var k = b.OpenSubKey(RegionControlUninstallKey);
                    if (Read(k) is { } r) return r;
                }
        }
        catch (Exception e) { ShellLog.Write($"updates: cannot read Region Control's uninstall key: {e.Message}"); }
        return null;

        static (string, string?)? Read(RegistryKey? k) =>
            k?.GetValue("DisplayVersion") is string v && v.Length > 0 ? (v, k.GetValue("InstallLocation") as string) : null;
    }
}

/// <summary>GET &lt;feed base&gt;/repos/&lt;owner&gt;/&lt;repo&gt;/releases with an ETag cache (contract 16.7).</summary>
public sealed class FeedClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private readonly Dictionary<string, List<FeedRelease>> _session = new(StringComparer.OrdinalIgnoreCase);

    public FeedClient(TimeSpan? timeout = null)
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskKit/{Program.Version}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _cacheFile = Path.Combine(UpdatePaths.UpdatesDir, "feed-cache.json");
    }

    public HttpClient Http => _http;

    public static string? RepoPath(string? repoUrl)
    {
        if (repoUrl == null || !Uri.TryCreate(repoUrl, UriKind.Absolute, out var u)) return null;
        if (!u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = u.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0) ? $"{parts[0]}/{parts[1]}" : null;
    }

    public async Task<List<FeedRelease>> ReleasesAsync(string repoUrl, CancellationToken ct = default)
    {
        var path = RepoPath(repoUrl) ?? throw new FeedException($"\"{repoUrl}\" is not a GitHub repo URL.");
        var url = $"{UpdateSources.FeedBase}/repos/{path}/releases?per_page=100";
        if (_session.TryGetValue(url, out var done)) return done;
        if (!UpdateSources.UrlAllowed(url)) throw new FeedException($"{url} is not HTTPS.");

        var cache = ReadCache();
        var cached = cache[url] as JsonObject;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if ((string?)cached?["etag"] is { Length: > 0 } etag) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new FeedException(e is HttpRequestException ? $"offline or unreachable ({e.Message})" : "timed out");
        }
        string body;
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotModified && (string?)cached?["body"] is { } cb) body = cb;
            else if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new FeedException($"GitHub rate limit reached (HTTP {(int)resp.StatusCode})");
            else if (resp.StatusCode == HttpStatusCode.NotFound) throw new FeedException($"no releases found ({path}: HTTP 404)");
            else if (!resp.IsSuccessStatusCode) throw new FeedException($"HTTP {(int)resp.StatusCode} from {path}");
            else
            {
                body = await resp.Content.ReadAsStringAsync(ct);
                var newTag = resp.Headers.ETag?.ToString();
                cache[url] = new JsonObject { ["etag"] = newTag, ["body"] = body, ["time"] = DateTimeOffset.Now.ToString("o") };
                WriteCache(cache);
            }
        }
        var list = Parse(body);
        _session[url] = list;
        return list;
    }

    public static List<FeedRelease> Parse(string body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException e) { throw new FeedException($"bad feed response: {e.Message}"); }
        if (root is not JsonArray arr) throw new FeedException("bad feed response: not a list of releases");
        var list = new List<FeedRelease>();
        foreach (var r in arr.OfType<JsonObject>())
        {
            var tag = (string?)r["tag_name"];
            if (string.IsNullOrEmpty(tag)) continue;
            var assets = (r["assets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(a => new ReleaseAsset((string?)a["name"] ?? "", (string?)a["browser_download_url"] ?? "", (string?)a["digest"]))
                .Where(a => a.Name.Length > 0 && a.Url.Length > 0).ToList();
            list.Add(new FeedRelease(tag, r["draft"] is JsonValue d && d.TryGetValue<bool>(out var dv) && dv, (string?)r["html_url"] ?? "", assets));
        }
        return list;
    }

    private JsonObject ReadCache()
    {
        try { if (File.Exists(_cacheFile) && JsonNode.Parse(File.ReadAllText(_cacheFile)) is JsonObject o) return o; }
        catch (Exception) { /* a broken cache is a cold cache */ }
        return new JsonObject();
    }

    private void WriteCache(JsonObject cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, cache.ToJsonString());
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShellLog.Write($"updates: cannot write feed cache: {e.Message}"); }
    }

    public void Dispose() => _http.Dispose();
}

public static class UpdatePaths
{
    public static string UpdatesDir => Path.Combine(ShellPaths.LocalAppData, "DeskKit", "updates");
}

public static class UpdateChecker
{
    /// <summary>The newest release for a component (contract 16.2), or null when none matches.</summary>
    public static (FeedRelease Release, SemVer Version)? Newest(IEnumerable<FeedRelease> releases, string tagPrefix, bool includePre)
    {
        (FeedRelease, SemVer)? best = null;
        foreach (var r in releases)
        {
            if (r.Draft || !r.Tag.StartsWith(tagPrefix, StringComparison.Ordinal)) continue;
            var v = SemVer.TryParse(r.Tag[tagPrefix.Length..]);
            if (v == null || (v.IsPrerelease && !includePre)) continue;
            if (best == null || v.CompareTo(best.Value.Item2) > 0) best = (r, v);
        }
        return best;
    }

    /// <summary>Check every component. Feed errors are per repo and never throw (contract 16.7).</summary>
    public static async Task<List<UpdateCheck>> CheckAsync(IEnumerable<UpdateComponent> components, bool includePre,
        FeedClient? client = null, CancellationToken ct = default)
    {
        var own = client == null;
        client ??= new FeedClient();
        var results = new List<UpdateCheck>();
        try
        {
            foreach (var c in components)
            {
                var check = new UpdateCheck { Component = c };
                results.Add(check);
                if (c.Repo == null || FeedClient.RepoPath(c.Repo) == null || c.AssetName == null)
                {
                    check.State = UpdateStateKind.NoSource;
                    check.Note = c.Repo == null ? "no update source (suite.json has no repo)" : c.AssetName == null ? "no asset rule" : "repo is not a GitHub URL";
                    continue;
                }
                List<FeedRelease> releases;
                try { releases = await client.ReleasesAsync(c.Repo, ct); }
                catch (FeedException e)
                {
                    check.State = UpdateStateKind.Error;
                    check.Note = e.Message;
                    continue;
                }
                var newest = Newest(releases, c.TagPrefix, includePre);
                var installed = SemVer.TryParse(c.InstalledVersion);
                if (newest == null || (installed != null && newest.Value.Version.CompareTo(installed) <= 0))
                {
                    check.State = UpdateStateKind.Current;
                    check.Note = newest == null ? "no release yet" : "";
                    continue;
                }
                var (rel, ver) = newest.Value;
                check.Available = ver.Text;
                check.Release = rel;
                var assetName = c.AssetName(ver.Text);
                check.Asset = rel.Assets.FirstOrDefault(a => a.Name == assetName);
                if (c.NotifyOnlyReason != null)
                {
                    check.State = UpdateStateKind.NotifyOnly;
                    check.Note = c.NotifyOnlyReason;
                }
                else if (check.Asset == null)
                {
                    check.State = UpdateStateKind.Error;
                    check.Note = $"release {rel.Tag} has no asset {assetName}";
                    ShellLog.Write($"updates: {c.Id}: {check.Note}");
                }
                else check.State = UpdateStateKind.Update;
            }
            var errors = results.Where(r => r.State == UpdateStateKind.Error).Select(r => $"{r.Component.Id}: {r.Note}").Distinct().ToList();
            ShellLog.Write($"updates: checked {results.Count} component(s): " +
                string.Join(", ", results.Select(r => $"{r.Component.Id} {StateWord(r.State)}{(r.Available != null ? " " + r.Available : "")}")));
            if (errors.Count > 0) ShellLog.Write("updates: check failed for " + string.Join("; ", errors));
        }
        finally { if (own) client.Dispose(); }
        return results;
    }

    public static string StateWord(UpdateStateKind s) => s switch
    {
        UpdateStateKind.Current => "current",
        UpdateStateKind.Update => "update",
        UpdateStateKind.NotifyOnly => "notify-only",
        UpdateStateKind.NoSource => "no-source",
        _ => "error",
    };
}
