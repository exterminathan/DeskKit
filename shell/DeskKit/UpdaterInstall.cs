// Installing one update (contract 16.3, 16.5, 16.6): download, SHA256 check, stage in
// <modules dir>\_update\<id>.new, ask before stopping a running module, swap by rename with a rollback copy,
// restart, record the result. The data dir is never touched.
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskKit.Shell;

public enum UpdateResult { Updated, Current, Failed, RolledBack, Declined, NotifyOnly, Started }

public sealed record UpdateOutcome(UpdateResult Result, string Message)
{
    public string Word => Result switch
    {
        UpdateResult.Updated => "updated",
        UpdateResult.Current => "current",
        UpdateResult.Failed => "failed",
        UpdateResult.RolledBack => "rolled-back",
        UpdateResult.Declined => "declined",
        UpdateResult.NotifyOnly => "notify-only",
        _ => "started",
    };
}

public static class UpdateInstaller
{
    public const string UpdateDirName = "_update";

    /// <summary>How long an update waits for another update or cleanup to finish. Tests may shorten it.</summary>
    public static TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Lock shared by every shell process (named mutex Local\DeskKit.Update): one update or one cleanup at a time.
    /// A Mutex must be released by the thread that took it, and an update awaits, so a dedicated thread owns it.
    /// </summary>
    public sealed class UpdateLock : IDisposable
    {
        public const string MutexName = @"Local\DeskKit.Update";
        private readonly ManualResetEventSlim _release;
        private UpdateLock(ManualResetEventSlim release) { _release = release; }

        /// <summary>Take the lock, waiting up to the timeout. Null when another process or thread holds it.</summary>
        public static UpdateLock? TryAcquire(TimeSpan timeout, string name = MutexName)
        {
            UpdateLock? result = null;
            using var got = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var t = new Thread(() =>
            {
                using var mutex = new Mutex(false, name);
                bool held;
                try { held = mutex.WaitOne(timeout); }
                catch (AbandonedMutexException) { held = true; }   // a crashed process held it: this thread owns it now
                if (held) result = new UpdateLock(release);
                got.Set();
                if (!held) return;
                release.Wait();
                mutex.ReleaseMutex();
            }) { IsBackground = true, Name = "DeskKit.UpdateLock" };
            t.Start();
            got.Wait();
            return result;
        }

        public void Dispose() => _release.Set();
    }

    /// <summary>
    /// At shell start: finish a swap a crash cut short (put &lt;id&gt;.old back if &lt;id&gt; is missing), then delete
    /// modules\_update\. Never throws.
    /// </summary>
    public static void CleanLeftovers(string modulesDir, string lockName = UpdateLock.MutexName)
    {
        var dir = Path.Combine(modulesDir, UpdateDirName);
        if (!Directory.Exists(dir)) return;
        // Another process is updating: its staging folders are in use, so leave them alone.
        using var gate = UpdateLock.TryAcquire(TimeSpan.Zero, lockName);
        if (gate == null) { ShellLog.Write("update: cleanup skipped, another update is running"); return; }
        try
        {
            foreach (var old in Directory.GetDirectories(dir, "*.old"))
            {
                var id = Path.GetFileName(old)[..^4];
                var target = Path.Combine(modulesDir, id);
                if (!Directory.Exists(target))
                {
                    Directory.Move(old, target);
                    ShellLog.Write($"update: restored {id} from an interrupted update");
                }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLog.Write($"update: cannot clean {dir}: {e.Message}");
        }
    }

    // ---------------- Download and checksum (16.3) ----------------

    public static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>The expected SHA256 of the asset: SHA256SUMS(.txt), else the asset digest. Refuses a conflict or none.</summary>
    public static async Task<string> ExpectedSha256Async(HttpClient http, FeedRelease rel, ReleaseAsset asset, string name, CancellationToken ct)
    {
        string? fromSums = null;
        var sums = rel.Assets.FirstOrDefault(a => a.Name is "SHA256SUMS.txt" or "SHA256SUMS");
        if (sums != null)
        {
            if (!UpdateSources.UrlAllowed(sums.Url)) throw new FeedException($"{sums.Url} is not HTTPS.");
            var text = await http.GetStringAsync(sums.Url, ct);
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*') == asset.Name) { fromSums = parts[0].ToLowerInvariant(); break; }
            }
        }
        string? fromDigest = asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? d["sha256:".Length..].ToLowerInvariant() : null;
        foreach (var h in new[] { fromSums, fromDigest })
            if (h != null && (h.Length != 64 || !h.All(Uri.IsHexDigit))) throw new FeedException($"{name} {asset.Name}: bad checksum \"{h}\".");
        if (fromSums != null && fromDigest != null && fromSums != fromDigest)
            throw new FeedException($"The checksums of {name} in SHA256SUMS and in the release do not agree. Nothing was changed.");
        return fromSums ?? fromDigest
            ?? throw new FeedException($"{name} {VersionOf(rel)} has no checksum. Download it yourself from {rel.HtmlUrl}");
    }

    private static string VersionOf(FeedRelease rel) => rel.Tag.Contains("/v") ? rel.Tag[(rel.Tag.IndexOf("/v") + 2)..] : rel.Tag.TrimStart('v');

    /// <summary>Download to %LOCALAPPDATA%\DeskKit\updates\&lt;id&gt;-&lt;version&gt;\&lt;asset&gt; and check it. Deletes it on a mismatch.</summary>
    public static async Task<string> DownloadAsync(FeedClient client, UpdateCheck c, CancellationToken ct)
    {
        var comp = c.Component;
        var asset = c.Asset!;
        if (!UpdateSources.UrlAllowed(asset.Url)) throw new FeedException($"The download address of {comp.Name} is not HTTPS.");
        var expected = await ExpectedSha256Async(client.Http, c.Release!, asset, comp.Name, ct);
        var dir = Path.Combine(UpdatePaths.UpdatesDir, $"{comp.Id}-{c.Available}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(asset.Name));
        var part = file + ".part";
        try
        {
            using (var resp = await client.Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode) throw new FeedException($"The download of {comp.Name} {c.Available} failed: HTTP {(int)resp.StatusCode}.");
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(part);
                await src.CopyToAsync(dst, ct);
            }
            File.Move(part, file, overwrite: true);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            TryDelete(part); TryDeleteDir(dir);
            throw new FeedException($"The download of {comp.Name} {c.Available} failed: {e.Message}");
        }
        var got = Sha256Of(file);
        if (got != expected)
        {
            TryDelete(file); TryDeleteDir(dir);
            ShellLog.Write($"update {comp.Id}: checksum mismatch, got {got}, expected {expected}");
            throw new ChecksumException($"The download of {comp.Name} {c.Available} did not match its checksum. Nothing was changed.");
        }
        return file;
    }

    public sealed class ChecksumException(string message) : Exception(message);

    // ---------------- One module (16.6) ----------------

    /// <summary>
    /// Update one module. askStop is asked only when the module runs; it returns true for "stop it and update".
    /// The controller is used for the running state, the stop protocol and the restart.
    /// </summary>
    public static async Task<UpdateOutcome> UpdateModuleAsync(UpdateCheck c, ModuleController controller,
        Func<string, bool> askStop, FeedClient? client = null, CancellationToken ct = default)
    {
        var comp = c.Component;
        if (c.State == UpdateStateKind.Current) return new(UpdateResult.Current, $"{comp.Name} {comp.InstalledVersion} is up to date.");
        if (c.State == UpdateStateKind.NotifyOnly)
            return new(UpdateResult.NotifyOnly, $"{comp.Name} {c.Available} is available. {c.Note}{(c.Release != null ? " " + c.Release.HtmlUrl : "")}");
        if (c.State != UpdateStateKind.Update || comp.Entry == null || c.Asset == null)
            return Record(comp, c.Available, UpdateResult.Failed, $"{comp.Name}: no update to install ({c.Note}).");

        using var gate = UpdateLock.TryAcquire(LockWait);
        if (gate == null)
            return Record(comp, c.Available, UpdateResult.Failed, $"{comp.Name}: another DeskKit update is running. Try again in a minute. Nothing was changed.");
        var own = client == null;
        client ??= new FeedClient(TimeSpan.FromMinutes(10));
        var modulesDir = controller.ModulesDir;
        var work = Path.Combine(modulesDir, UpdateDirName);
        var staged = Path.Combine(work, comp.Id + ".new");
        var old = Path.Combine(work, comp.Id + ".old");
        var target = comp.Entry.Folder;
        string? download = null;
        try
        {
            // 1-2. Download and check.
            try { download = await DownloadAsync(client, c, ct); }
            catch (ChecksumException e) { return Record(comp, c.Available, UpdateResult.Failed, e.Message); }
            catch (FeedException e) { return Record(comp, c.Available, UpdateResult.Failed, e.Message); }

            // 3. Stage.
            try { Stage(comp, c.Available!, download, target, staged, work); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or StageException)
            {
                TryDeleteDir(staged);
                TryDeleteDir(Path.Combine(work, comp.Id + ".extract"));
                TryRemoveIfEmpty(work);
                return Record(comp, c.Available, UpdateResult.Failed, $"{comp.Name} {c.Available} could not be prepared: {e.Message} Nothing was changed.");
            }

            // 4. Running? Ask first.
            var entry = controller.Find(comp.Id) ?? comp.Entry;
            bool wasRunning = controller.IsRunning(entry);
            if (wasRunning)
            {
                var m = entry.Manifest!;
                if (!RunModes.IsLongRunning(m.RunMode))
                {
                    TryDeleteDir(staged);
                    return Record(comp, c.Available, UpdateResult.Declined, $"{comp.Name} is busy. Try again when it finishes.");
                }
                if (!askStop($"{comp.Name} is running. Stop it, update it to {c.Available} and start it again?"))
                {
                    TryDeleteDir(staged);
                    return Record(comp, c.Available, UpdateResult.Declined, $"{comp.Name} is running; the update to {c.Available} waits until you agree.");
                }
                var stopMsg = await controller.StopAsync(entry);
                if (stopMsg != null || controller.IsRunning(entry))
                {
                    TryDeleteDir(staged);
                    return Record(comp, c.Available, UpdateResult.Declined, $"{stopMsg ?? comp.Name + " did not stop."} Nothing was changed.");
                }
            }

            // 5. Swap. After a stop, Windows may hold the files a moment longer: retry the first rename for 5 s.
            var swap = await SwapAsync(comp, target, staged, old, wasRunning ? TimeSpan.FromSeconds(5) : TimeSpan.Zero);
            if (swap != null)
            {
                TryDeleteDir(staged);
                if (wasRunning) RestartAfter(controller, comp.Id);
                return swap;
            }

            // 6. Clean up.
            TryDeleteDir(old);
            TryDeleteDir(Path.GetDirectoryName(download)!);
            download = null;

            // 8-9. Rescan, restart, record.
            controller.Reload();
            string restart = "";
            if (wasRunning) restart = RestartAfter(controller, comp.Id) is { } err ? $" It did not start again: {err}" : " It was started again.";
            return Record(comp, c.Available, UpdateResult.Updated, $"{comp.Name} was updated from {comp.InstalledVersion} to {c.Available}.{restart}");
        }
        finally
        {
            if (download != null) TryDeleteDir(Path.GetDirectoryName(download)!);
            TryRemoveIfEmpty(work);
            if (own) client.Dispose();
        }
    }

    private static string? RestartAfter(ModuleController controller, string id)
    {
        controller.Reload();
        var e = controller.Find(id);
        if (e == null) return "the module is missing after the update";
        var err = controller.Start(e);
        ShellLog.Write($"update {id}: restart {(err ?? "ok")}");
        return err;
    }

    private sealed class StageException(string message) : Exception(message);

    private static void Stage(UpdateComponent comp, string version, string download, string current, string staged, string work)
    {
        Directory.CreateDirectory(work);
        TryDeleteDir(staged);
        if (Directory.Exists(staged)) throw new StageException($"{staged} cannot be removed.");
        var m = comp.Entry!.Manifest!;
        if (comp.Kind == ComponentKind.InRepo)
        {
            var extract = Path.Combine(work, comp.Id + ".extract");
            TryDeleteDir(extract);
            ZipFile.ExtractToDirectory(download, extract);   // rejects entries that leave the folder
            var top = Path.Combine(extract, comp.Id);
            if (!Directory.Exists(top)) { TryDeleteDir(extract); throw new StageException($"the zip has no top folder {comp.Id}\\."); }
            Directory.Move(top, staged);
            TryDeleteDir(extract);
        }
        else
        {
            CopyDir(current, staged);
            if (download.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(download, Path.Combine(staged, "bin"), overwriteFiles: true);
            else
            {
                var exe = Path.Combine(staged, m.Entry.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                File.Copy(download, exe, overwrite: true);
            }
            // The only key the shell ever writes: a pinned module's version (contract 16.6 step 3).
            var mf = Path.Combine(staged, "module.json");
            var node = JsonNode.Parse(File.ReadAllText(mf)) as JsonObject ?? throw new StageException("module.json is not an object.");
            node["version"] = version;
            File.WriteAllText(mf, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
        }

        // Check the staged folder.
        var file = Path.Combine(staged, "module.json");
        if (!File.Exists(file)) throw new StageException("the new version has no module.json.");
        var j = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new StageException("module.json is not an object.");
        if ((string?)j["id"] != comp.Id) throw new StageException($"its module.json id is \"{(string?)j["id"]}\", not \"{comp.Id}\".");
        if ((string?)j["version"] != version) throw new StageException($"its module.json version is \"{(string?)j["version"]}\", not \"{version}\".");
        var entry = (string?)j["entry"] ?? "";
        if (entry.Length == 0 || entry.Contains("..") || Path.IsPathRooted(entry) || entry.Contains(':') || !File.Exists(Path.Combine(staged, entry.Replace('/', '\\'))))
            throw new StageException($"its entry \"{entry}\" is missing.");
    }

    /// <summary>Rename current to .old, staged to current; roll back if the second rename fails. Null on success.</summary>
    private static async Task<UpdateOutcome?> SwapAsync(UpdateComponent comp, string target, string staged, string old, TimeSpan retry)
    {
        TryDeleteDir(old);
        var until = DateTime.UtcNow + retry;
        while (true)
        {
            try { Directory.Move(target, old); break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow < until) { await Task.Delay(250); continue; }
                ShellLog.Write($"update {comp.Id}: rename {target} failed: {e.Message}");
                return Record(comp, VersionFromStaged(staged), UpdateResult.Failed, $"{comp.Name} could not be updated: a file is in use. Nothing was changed.");
            }
        }
        try
        {
            if (Environment.GetEnvironmentVariable("DESKKIT_UPDATE_FAIL_AT") == "swap-in")
                throw new IOException("DESKKIT_UPDATE_FAIL_AT=swap-in (test)");
            Directory.Move(staged, target);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLog.Write($"update {comp.Id}: rename into place failed: {e.Message}; rolling back");
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(old, target);
            }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException)
            {
                return Record(comp, VersionFromStaged(staged), UpdateResult.Failed,
                    $"{comp.Name}: the update and the rollback failed ({e2.Message}). The old copy is in {old}; DeskKit restores it at its next start.");
            }
            return Record(comp, VersionFromStaged(staged), UpdateResult.RolledBack, $"{comp.Name} could not be updated ({e.Message}). The old version was put back.");
        }
    }

    private static string? VersionFromStaged(string staged)
    {
        try { return (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(staged, "module.json")))?["version"]; }
        catch (Exception) { return null; }
    }

    private static UpdateOutcome Record(UpdateComponent comp, string? to, UpdateResult r, string message)
    {
        var o = new UpdateOutcome(r, message);
        if (r is UpdateResult.Updated or UpdateResult.Failed or UpdateResult.RolledBack or UpdateResult.Declined)
            UpdaterState.RecordResult(comp.Id, comp.InstalledVersion, to, o.Word, message);
        return o;
    }

    // ---------------- The shell itself (16.5) ----------------

    /// <summary>Download and check DeskKit-Setup-X.Y.Z.exe, then start it (unless DESKKIT_UPDATE_NO_RUN=1).</summary>
    public static async Task<UpdateOutcome> UpdateShellAsync(UpdateCheck c, CancellationToken ct = default)
    {
        var comp = c.Component;
        if (c.State == UpdateStateKind.Current) return new(UpdateResult.Current, $"DeskKit {comp.InstalledVersion} is up to date.");
        if (c.State != UpdateStateKind.Update || c.Asset == null) return Record(comp, c.Available, UpdateResult.Failed, $"DeskKit: no update to install ({c.Note}).");
        using var client = new FeedClient(TimeSpan.FromMinutes(10));
        string file;
        try { file = await DownloadAsync(client, c, ct); }
        catch (Exception e) when (e is ChecksumException or FeedException) { return Record(comp, c.Available, UpdateResult.Failed, e.Message); }
        if (Environment.GetEnvironmentVariable("DESKKIT_UPDATE_NO_RUN") == "1")
        {
            ShellLog.Write($"update shell: downloaded and checked {file}; not started (DESKKIT_UPDATE_NO_RUN)");
            return new(UpdateResult.Started, $"Downloaded and checked {file}. Not started (DESKKIT_UPDATE_NO_RUN=1).");
        }
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = true };
            foreach (var a in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART" }) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception e)
        {
            return Record(comp, c.Available, UpdateResult.Failed, $"The DeskKit {c.Available} installer could not start: {e.Message}");
        }
        UpdaterState.RecordResult(comp.Id, comp.InstalledVersion, c.Available, "started", $"installer {file} started");
        return new(UpdateResult.Started, $"The DeskKit {c.Available} installer is running. DeskKit closes now and starts again when it finishes.");
    }

    // ---------------- Files ----------------

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch (Exception) { } }

    private static void TryRemoveIfEmpty(string d)
    {
        try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (Exception) { }
    }

    private static void TryDeleteDir(string d)
    {
        try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch (Exception) { }
    }
}
