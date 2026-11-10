// DeskKit.exe --selftest: manifest scanning, --list on this repo's modules, run-mode rules, command
// building, helper detection, autostart state, and real start/stop of test modules (shell\tests\fixtures).
// Everything runs against temp folders: APPDATA, LOCALAPPDATA and the Run key are redirected.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DeskKit.Shell;

public static class SelfTest
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == null ? "" : "  -> " + detail)}");
    }

    public static int Run(string? modulesDirArg)
    {
        var temp = Path.Combine(Path.GetTempPath(), "deskkit-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(temp, "Roaming"));
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(temp, "Local"));
        var runKey = @"Software\DeskKit-SelfTest\Run-" + Environment.ProcessId;
        Environment.SetEnvironmentVariable("DESKKIT_RUN_KEY", runKey);
        Environment.SetEnvironmentVariable("DESKKIT_NO_UI", "1");
        Console.WriteLine($"DeskKit {Program.Version} self-test, temp {temp}");

        try
        {
            var repoModules = Program.ResolveModulesDir(modulesDirArg);
            var repoRoot = Path.GetDirectoryName(repoModules)!;
            TestRepoScan(repoRoot, repoModules);
            TestBrokenAndSkipped(temp);
            TestList(repoRoot, repoModules);
            TestRunModes();
            TestParse();
            TestCommands(temp);
            TestPythonLookup(temp);
            TestQuoting();
            TestHelpers(temp);
            TestState(temp);
            TestLogs(temp);
            TestUpdates(temp);
            var fixture = Path.Combine(repoRoot, "shell", "tests", "fixtures", "fixture.ps1");
            if (File.Exists(fixture)) TestProcesses(temp, fixture).GetAwaiter().GetResult();
            else Check(@"process tests need shell\tests\fixtures\fixture.ps1", false, fixture + " not found");
            var echo = Path.Combine(repoRoot, "shell", "tests", "fixtures", "echo.ps1");
            if (File.Exists(echo)) TestPassThrough(temp, echo).GetAwaiter().GetResult();
            else Check(@"pass-through tests need shell\tests\fixtures\echo.ps1", false, echo + " not found");
        }
        catch (Exception ex)
        {
            Check("self-test ran to the end", false, ex.ToString());
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\DeskKit-SelfTest", throwOnMissingSubKey: false); } catch (Exception) { }
            try { Directory.Delete(temp, recursive: true); } catch (Exception) { Console.WriteLine($"note: could not delete {temp}"); }
        }
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------- Updates (contract 16) ----------------

    private static void TestUpdates(string temp)
    {
        Check("semver: 0.2.0 > 0.1.9", SemVer.Compare("0.2.0", "0.1.9") > 0);
        Check("semver: 0.10.0 > 0.9.0 (numbers, not text)", SemVer.Compare("0.10.0", "0.9.0") > 0);
        Check("semver: 0.2.0 > 0.2.0-beta.1", SemVer.Compare("0.2.0", "0.2.0-beta.1") > 0);
        Check("semver: beta.2 > beta.1, beta.10 > beta.9", SemVer.Compare("1.0.0-beta.2", "1.0.0-beta.1") > 0 && SemVer.Compare("1.0.0-beta.10", "1.0.0-beta.9") > 0);
        Check("semver: alpha < alpha.1 < beta (SemVer 11.4)", SemVer.Compare("1.0.0-alpha", "1.0.0-alpha.1") < 0 && SemVer.Compare("1.0.0-alpha.1", "1.0.0-beta") < 0);
        Check("semver: numeric identifier < text identifier", SemVer.Compare("1.0.0-1", "1.0.0-a") < 0);
        Check("semver: build metadata ignored", SemVer.Compare("1.0.0+abc", "1.0.0") == 0);
        Check("semver: v prefix and junk are not versions", SemVer.TryParse("v1.0.0") == null && SemVer.TryParse("1.0") == null && SemVer.TryParse("01.0.0") == null);

        var rels = new List<FeedRelease>
        {
            new("eyesaver/v0.3.0-rc.1", false, "", Array.Empty<ReleaseAsset>()),
            new("eyesaver/v0.2.0", false, "", Array.Empty<ReleaseAsset>()),
            new("eyesaver/v0.9.0", true, "", Array.Empty<ReleaseAsset>()),          // draft
            new("regionshot/v5.0.0", false, "", Array.Empty<ReleaseAsset>()),
            new("v1.0.0", false, "", Array.Empty<ReleaseAsset>()),
            new("eyesaver/vjunk", false, "", Array.Empty<ReleaseAsset>()),
        };
        Check("feed: newest eyesaver skips drafts, other modules and pre-releases", UpdateChecker.Newest(rels, "eyesaver/v", false)?.Version.Text == "0.2.0");
        Check("feed: pre-release offered when opted in", UpdateChecker.Newest(rels, "eyesaver/v", true)?.Version.Text == "0.3.0-rc.1");
        Check("feed: shell tag rule v only matches vX.Y.Z", UpdateChecker.Newest(rels, "v", false)?.Version.Text == "1.0.0");
        Check("feed: repo URL parse", FeedClient.RepoPath("https://github.com/exterminathan/DeskKit") == "exterminathan/DeskKit"
            && FeedClient.RepoPath("https://example.com/a/b") == null && FeedClient.RepoPath(null) == null);
        var parsed = FeedClient.Parse("[{\"tag_name\":\"v1.0.0\",\"draft\":false,\"html_url\":\"h\",\"assets\":[{\"name\":\"a.zip\",\"browser_download_url\":\"https://x/a.zip\",\"digest\":\"sha256:00\"}]}]");
        Check("feed: GitHub JSON parses (tag, asset, digest)", parsed.Count == 1 && parsed[0].Assets[0].Digest == "sha256:00");
        var rule = UpdateSources.PinnedAssetRule(new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = "0.1.1", ["url"] = "https://github.com/o/r/releases/download/v0.1.1/RegionControl-Portable-0.1.1.exe",
        });
        Check("feed: pinned asset rule without asset_pattern replaces the version in the url's file name", rule?.Invoke("0.2.0") == "RegionControl-Portable-0.2.0.exe");
        Environment.SetEnvironmentVariable("DESKKIT_FEED_BASE", null);
        Check("feed: plain http refused without DESKKIT_FEED_BASE", !UpdateSources.UrlAllowed("http://127.0.0.1:1/x") && UpdateSources.UrlAllowed("https://github.com/x"));

        // state.json: autostart (ShellState) and updates (UpdaterState) keep each other's keys.
        var st = new ShellState(ShellPaths.StateFile);
        st.SetAutostart("alpha", true);
        UpdaterState.CheckEnabled = false;
        UpdaterState.RecordResult("alpha", "0.1.0", "0.2.0", "updated", "");
        st.SetAutostart("beta", false);
        var again = new ShellState(ShellPaths.StateFile);
        Check("state: updates survive an autostart write", !UpdaterState.CheckEnabled && UpdaterState.LastResult("alpha")?.Result == "updated");
        Check("state: autostart survives an updates write", again.HasEntry("alpha") && again.HasEntry("beta"));
        UpdaterState.CheckEnabled = true;

        // Leftover swap: <id>.old comes back when <id> is missing, then _update is removed.
        var mods = Path.Combine(temp, "upd-mods");
        Directory.CreateDirectory(Path.Combine(mods, "_update", "lost.old"));
        File.WriteAllText(Path.Combine(mods, "_update", "lost.old", "module.json"), "{}");
        UpdateInstaller.CleanLeftovers(mods);
        Check("update: interrupted swap restored at start", File.Exists(Path.Combine(mods, "lost", "module.json")) && !Directory.Exists(Path.Combine(mods, "_update")));
        Check("update: write test leaves no _update folder", UpdateSources.IsWritable(mods) && !Directory.Exists(Path.Combine(mods, "_update")));

        // Update lock (defect 9): one holder at a time; cleanup skips while an update holds it.
        var lockName = @"Local\DeskKit.Update.selftest." + Guid.NewGuid().ToString("N");
        var lockMods = Path.Combine(temp, "lock-mods");
        Directory.CreateDirectory(Path.Combine(lockMods, "_update", "busy.old"));
        File.WriteAllText(Path.Combine(lockMods, "_update", "busy.old", "module.json"), "{}");
        var first = UpdateInstaller.UpdateLock.TryAcquire(TimeSpan.Zero, lockName);
        Check("update lock: first caller gets it", first != null);
        Check("update lock: second caller is refused while held", UpdateInstaller.UpdateLock.TryAcquire(TimeSpan.FromMilliseconds(200), lockName) == null);
        UpdateInstaller.CleanLeftovers(lockMods, lockName);
        Check("update lock: cleanup leaves staging alone while an update holds the lock",
            Directory.Exists(Path.Combine(lockMods, "_update", "busy.old")) && !Directory.Exists(Path.Combine(lockMods, "busy")));
        first?.Dispose();
        var third = UpdateInstaller.UpdateLock.TryAcquire(TimeSpan.FromSeconds(5), lockName);
        Check("update lock: free again after release", third != null);
        third?.Dispose();
        UpdateInstaller.CleanLeftovers(lockMods, lockName);
        Check("update lock: cleanup runs once the lock is free", File.Exists(Path.Combine(lockMods, "busy", "module.json")) && !Directory.Exists(Path.Combine(lockMods, "_update")));
    }

    // ---------------- Manifests ----------------

    private static void TestRepoScan(string repoRoot, string modulesDir)
    {
        var suiteFile = Path.Combine(repoRoot, "suite.json");
        Check("repo: suite.json found next to modules\\", File.Exists(suiteFile), suiteFile);
        if (!File.Exists(suiteFile)) return;
        var suite = JsonNode.Parse(File.ReadAllText(suiteFile))!["modules"]!.AsArray()
            .Where(n => (string?)n!["status"] != "planned")
            .Select(n => (id: (string)n!["id"]!, version: (string)n["version"]!)).ToList();
        var plannedIds = JsonNode.Parse(File.ReadAllText(suiteFile))!["modules"]!.AsArray()
            .Where(n => (string?)n!["status"] == "planned").Select(n => (string)n!["id"]!).ToList();
        var found = ModuleScanner.Scan(modulesDir);
        Check("repo: scan finds every suite.json module",
            suite.All(s => found.Any(f => f.Id == s.id)), string.Join(",", found.Select(f => f.Id)));
        Check("repo: scan skips _template folders", found.All(f => !f.FolderName.StartsWith('_')));
        Check("repo: scan skips planned modules", found.All(f => !plannedIds.Contains(f.FolderName)), string.Join(",", plannedIds));
        foreach (var f in found)
        {
            Check($"repo: {f.Id} parses", !f.IsBroken, f.Error);
            var s = suite.FirstOrDefault(x => x.id == f.Id);
            if (s.id != null && !f.IsBroken)
                Check($"repo: {f.Id} version equals suite.json", s.version == f.Manifest!.Version, $"{f.Manifest!.Version} vs {s.version}");
        }
    }

    private static void TestBrokenAndSkipped(string temp)
    {
        var dir = Path.Combine(temp, "scan");
        WriteModule(dir, "good", Fixture("good", "oneshot", "hello"));
        WriteModule(dir, "_hidden", Fixture("hidden", "oneshot", "hello"));
        Directory.CreateDirectory(Path.Combine(dir, "bad"));
        File.WriteAllText(Path.Combine(dir, "bad", "module.json"), "{ \"id\": \"bad\", ");
        WriteModule(dir, "wrongid", Fixture("other", "oneshot", "hello"));
        Directory.CreateDirectory(Path.Combine(dir, "nomanifest"));

        var list = ModuleScanner.Scan(dir);
        Check("scan: folders starting with _ are skipped", list.All(m => m.FolderName != "_hidden"));
        Check("scan: a folder with no module.json is skipped", list.All(m => m.FolderName != "nomanifest"));
        Check("scan: a good module parses", list.Any(m => m.Id == "good" && !m.IsBroken));
        var bad = list.FirstOrDefault(m => m.FolderName == "bad");
        Check("scan: invalid JSON shows as broken with the parse error", bad is { IsBroken: true, Error.Length: > 0 }, bad?.Error);
        var wrong = list.FirstOrDefault(m => m.FolderName == "wrongid");
        Check("scan: id that differs from the folder is broken", wrong is { IsBroken: true }, wrong?.Error);
        Check("diagnose: broken module says so", Runtimes.Diagnose(bad!)?.StartsWith("Broken module.json") == true);
        TestPlanned(temp);
        Check("scan: a missing modules dir gives an empty list", ModuleScanner.Scan(Path.Combine(temp, "none")).Count == 0);
    }

    /// <summary>A module that suite.json marks "status": "planned" is not scanned, listed or update-checked.</summary>
    private static void TestPlanned(string temp)
    {
        var root = Path.Combine(temp, "plannedrepo");
        var dir = Path.Combine(root, "modules");
        WriteModule(dir, "live", Fixture("live", "oneshot", "hello"));
        WriteModule(dir, "soon", Fixture("soon", "oneshot", "hello"));
        File.WriteAllText(Path.Combine(root, "suite.json"),
            "{ \"modules\": [ { \"id\": \"live\", \"version\": \"1.0.0\", \"source\": \"in-repo\", \"path\": \"modules/live\" },"
            + " { \"id\": \"soon\", \"version\": \"0.0.1\", \"source\": \"in-repo\", \"path\": \"modules/soon\", \"status\": \"planned\" } ] }");
        var list = ModuleScanner.Scan(dir);
        Check("planned: scan keeps the module that is not planned", list.Any(m => m.Id == "live"));
        Check("planned: scan skips a module marked planned in suite.json", list.All(m => m.Id != "soon"), string.Join(",", list.Select(m => m.Id)));
        var text = Program.ListText(dir);
        Check("planned: --list shows live and not soon", text.Contains("live ") && !text.Contains("soon "), text);
        var comps = UpdateSources.Components(dir, list);
        Check("planned: --check-updates has no component for soon", comps.All(c => c.Id != "soon") && comps.Any(c => c.Id == "live"), string.Join(",", comps.Select(c => c.Id)));
    }

    private static void TestList(string repoRoot, string modulesDir)
    {
        var text = Program.ListText(modulesDir);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Check("--list: header line", lines.Count > 1 && lines[1].StartsWith("ID") && lines[1].Contains("STATUS"), lines.ElementAtOrDefault(1));
        foreach (var e in ModuleScanner.Scan(modulesDir))
            Check($"--list: row for {e.Id} with its version",
                lines.Any(l => l.StartsWith(e.Id + " ") && l.Contains(e.Manifest?.Version ?? "-")), text);
        Check("--list: no template rows", !lines.Any(l => l.StartsWith("template")));
        var rc = lines.FirstOrDefault(l => l.StartsWith("regioncontrol "));
        if (rc != null && !File.Exists(Path.Combine(modulesDir, "regioncontrol", "bin", "RegionControl.exe")))
            Check("--list: pinned module without its release says Not installed", rc.Contains("Not installed"), rc);
        Console.WriteLine("---- --list output ----");
        Console.Write(text);
        Console.WriteLine("-----------------------");
    }

    // ---------------- Run modes ----------------

    private static void TestRunModes()
    {
        Check("mode: tray toggle starts when stopped", RunModes.Toggle("tray", false, false) == ToggleKind.Start);
        Check("mode: tray toggle stops when running", RunModes.Toggle("tray", true, false) == ToggleKind.Stop);
        Check("mode: window toggle stops when running", RunModes.Toggle("window", true, true) == ToggleKind.Stop);
        Check("mode: oneshot toggle runs the toggle action", RunModes.Toggle("oneshot", false, true) == ToggleKind.StartToggleAction);
        Check("mode: oneshot without toggle action starts args", RunModes.Toggle("oneshot", true, false) == ToggleKind.Start);
        Check("mode: popup toggle always starts", RunModes.Toggle("popup", true, true) == ToggleKind.Start);
        Check("mode: autostart only tray and window",
            RunModes.AutostartAllowed("tray") && RunModes.AutostartAllowed("window") && !RunModes.AutostartAllowed("popup") && !RunModes.AutostartAllowed("oneshot"));
        Check("mode: stop offered only for tray and window",
            RunModes.StopOffered("tray") && RunModes.StopOffered("window") && !RunModes.StopOffered("popup") && !RunModes.StopOffered("oneshot"));
        Check("mode: tray with mutex is running when the mutex exists", RunModes.IsRunning("tray", "m", _ => true, false));
        Check("mode: tray with mutex ignores the shell's own process", !RunModes.IsRunning("tray", "m", _ => false, true));
        Check("mode: pinned tray with no mutex runs only as the shell's process",
            RunModes.IsRunning("tray", null, _ => true, true) && !RunModes.IsRunning("tray", null, _ => true, false));
        Check("mode: oneshot runs while the shell's process lives",
            RunModes.IsRunning("oneshot", null, _ => true, true) && !RunModes.IsRunning("oneshot", null, _ => true, false));
        Check("mode: can stop with a stop event even if not started by the shell", RunModes.CanStop("window", "e", false));
        Check("mode: cannot stop a foreign copy without a stop event", !RunModes.CanStop("tray", null, false));
        Check("mode: can stop its own process without a stop event", RunModes.CanStop("tray", null, true));
        Check("mode: labels", RunModes.MainControlLabel("tray", true) == "Turn off" && RunModes.MainControlLabel("window", false) == "Open"
                              && RunModes.MainControlLabel("oneshot", false) == "Run" && RunModes.MainControlLabel("popup", false) == "Open");
    }

    // ---------------- Command line ----------------

    private static void TestParse()
    {
        var o = Program.Parse(new[] { "--run", "taskbargroups", "--", "Tools" });
        Check("args: --run id -- x passes x through", o.Run == "taskbargroups" && o.Extra!.SequenceEqual(new[] { "Tools" }));
        o = Program.Parse(new[] { "--modules-dir", "m", "--run", "x", "--", "--list", "--action", "y", "", "a b" });
        Check("args: everything after -- passes through, even options and empty strings",
            o.Extra!.SequenceEqual(new[] { "--list", "--action", "y", "", "a b" }) && !o.List && o.Action == null && o.ModulesDir == "m",
            string.Join("|", o.Extra!));
        o = Program.Parse(new[] { "--run", "x", "--" });
        Check("args: a bare -- at the end gives no extra args", o.Extra is { Count: 0 });
        o = Program.Parse(new[] { "--run", "x" });
        Check("args: no -- means no extra args", o.Extra == null);
        Check("args: -- and --action do not combine", Throws(() => Program.Parse(new[] { "--run", "x", "--action", "y", "--", "z" }), "do not combine"));
        Check("args: -- needs --run", Throws(() => Program.Parse(new[] { "--list", "--", "z" }), "needs --run"));
        Check("args: an unknown option is refused", Throws(() => Program.Parse(new[] { "--bogus" }), "Unknown argument"));
    }

    private static bool Throws(Action a, string text)
    {
        try { a(); return false; }
        catch (ArgumentException ex) { return ex.Message.Contains(text); }
    }

    // ---------------- Commands ----------------

    private static void TestCommands(string temp)
    {
        var dir = Path.Combine(temp, "cmd");
        var pyJson = Fixture("pyapp", "window", "x");
        pyJson["runtime"] = new JsonObject { ["type"] = "python", ["min"] = "3.11", ["requirements"] = null };
        pyJson["entry"] = "src/app.pyw";
        pyJson["args"] = new JsonArray("--flag", "{data_dir}\\out");
        WriteModule(dir, "pyapp", pyJson);
        var py = ModuleScanner.Load(Path.Combine(dir, "pyapp", "module.json"));

        var fakePy = Path.Combine(temp, "py", "python.exe");
        Runtimes.SetPythonCache(new List<Runtimes.PythonInstall>
        {
            new(Path.Combine(temp, "old", "python.exe"), Path.Combine(temp, "old", "pythonw.exe"), new Version(3, 10, 4)),
            new(fakePy, Path.Combine(temp, "py", "pythonw.exe"), new Version(3, 12, 1)),
        });
        try
        {
            var c = Runtimes.BuildCommand(py, py.Manifest!.Args);
            var dataDir = Path.Combine(temp, "Roaming", "DeskKit", "pyapp");
            Check("tokens: data_dir expands %APPDATA%", Runtimes.ExpandDataDir(py) == dataDir, Runtimes.ExpandDataDir(py));
            Check("python: skips a Python older than min", c.FileName.StartsWith(Path.Combine(temp, "py")), c.FileName);
            Check("python: console false uses pythonw.exe", c.FileName.EndsWith("pythonw.exe"), c.FileName);
            Check("python: entry first, then args with tokens",
                c.Arguments.SequenceEqual(new[] { Path.Combine(dir, "pyapp", "src", "app.pyw"), "--flag", dataDir + "\\out" }),
                string.Join(" | ", c.Arguments));
            Check("python: working dir is the module dir", c.WorkingDir == Path.Combine(dir, "pyapp"));
            var cx = Runtimes.BuildCommand(py, py.Manifest!.Args, new[] { "{data_dir}", "%APPDATA%", "x y" });
            Check("pass-through: extra args follow args and stay literal (no token or variable expansion)",
                cx.Arguments.SequenceEqual(new[] { Path.Combine(dir, "pyapp", "src", "app.pyw"), "--flag", dataDir + "\\out", "{data_dir}", "%APPDATA%", "x y" }),
                string.Join(" | ", cx.Arguments));
            py.Manifest.Console = true;
            Check("python: console true uses python.exe", Runtimes.BuildCommand(py, Array.Empty<string>()).FileName == fakePy);

            Runtimes.SetPythonCache(new List<Runtimes.PythonInstall>());
            try { Runtimes.BuildCommand(py, Array.Empty<string>()); Check("python: missing Python throws", false); }
            catch (RuntimeMissingException ex)
            {
                Check("python: missing Python gives the exact contract message",
                    ex.Message == "Python 3.11 or newer not found. Install it from https://www.python.org/downloads/windows/ and try again.", ex.Message);
            }
        }
        finally { Runtimes.SetPythonCache(null); }

        var psJson = Fixture("psapp", "oneshot", "hello");
        WriteModule(dir, "psapp", psJson);
        var ps = ModuleScanner.Load(Path.Combine(dir, "psapp", "module.json"));
        var pc = Runtimes.BuildCommand(ps, new[] { "a b" });
        Check("powershell: 5.1 uses Windows PowerShell", pc.FileName.EndsWith(@"System32\WindowsPowerShell\v1.0\powershell.exe", StringComparison.OrdinalIgnoreCase), pc.FileName);
        Check("powershell: console false adds hidden flags before -File",
            pc.Arguments.SequenceEqual(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-NonInteractive", "-WindowStyle", "Hidden", "-File",
                Path.Combine(dir, "psapp", "src", "fixture.ps1"), "a b" }), string.Join(" | ", pc.Arguments));
        Check("powershell: console false starts with no console", !pc.NewConsole);
        ps.Manifest!.Console = true;
        var pc2 = Runtimes.BuildCommand(ps, Array.Empty<string>());
        Check("powershell: console true has no hidden flags and a console", !pc2.Arguments.Contains("Hidden") && pc2.NewConsole);
        ps.Manifest.Runtime.Min = "7.2";
        var pwsh = Runtimes.FindOnPath("pwsh.exe");
        try
        {
            var pc3 = Runtimes.BuildCommand(ps, Array.Empty<string>());
            Check("powershell: min 7 uses pwsh.exe from PATH", pwsh != null && pc3.FileName == pwsh, pc3.FileName);
        }
        catch (RuntimeMissingException ex)
        {
            Check("powershell: min 7 without pwsh says how to install it", pwsh == null && ex.Message.Contains("PowerShell 7.2"), ex.Message);
        }

        var dnJson = Fixture("dnapp", "tray", "x");
        dnJson["runtime"] = new JsonObject { ["type"] = "dotnet", ["min"] = "8.0", ["self_contained"] = true };
        dnJson["entry"] = "bin/App.exe";
        WriteModule(dir, "dnapp", dnJson);
        var dn = ModuleScanner.Load(Path.Combine(dir, "dnapp", "module.json"));
        var dc = Runtimes.BuildCommand(dn, new[] { "--x" });
        Check("dotnet: runs the entry exe with args", dc.FileName == Path.Combine(dir, "dnapp", "bin", "App.exe") && dc.Arguments.SequenceEqual(new[] { "--x" }));
        Check("diagnose: a missing entry says Not built", Runtimes.Diagnose(dn)?.StartsWith("Not built") == true, Runtimes.Diagnose(dn));
        Check("dotnet: message names the runtime and the page",
            Runtimes.DotnetMissingMessage("App", "8.0") == "App needs the .NET 8.0 Desktop Runtime. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and try again.");
    }

    /// <summary>The real lookup (no cache): a PATH with no Python and no py launcher finds nothing.</summary>
    private static void TestPythonLookup(string temp)
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var empty = Path.Combine(temp, "empty-path");
        Directory.CreateDirectory(empty);
        try
        {
            Runtimes.SetPythonCache(null);
            Environment.SetEnvironmentVariable("PATH", empty);
            Check("python lookup: a PATH without Python finds none", Runtimes.PythonInstalls().Count == 0,
                string.Join(", ", Runtimes.PythonInstalls().Select(p => p.PythonExe)));
            Runtimes.SetPythonCache(null);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            var found = Runtimes.PythonInstalls();
            Console.WriteLine("note: Python found on this PC: " + (found.Count == 0 ? "none" : string.Join(", ", found.Select(p => $"{p.Version} {p.PythonExe}"))));
            Check("python lookup: no Store stub (WindowsApps) is ever used",
                found.All(p => !p.PythonExe.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Runtimes.SetPythonCache(null);
        }
    }

    private static void TestQuoting()
    {
        var args = new[] { "plain", "a b", "", "x\"y", @"C:\path with space\", @"back\\slash", "tab\there", @"\\server\share" };
        var line = CommandLineBuilder.Build(@"C:\Program Files\X\x.exe", args);
        var back = Native.SplitCommandLine(line);
        Check("quoting: every argument survives the round trip",
            back.Length == args.Length + 1 && back.Skip(1).SequenceEqual(args) && back[0] == @"C:\Program Files\X\x.exe", line);
    }

    // ---------------- Helpers ----------------

    private static void TestHelpers(string temp)
    {
        var helpers = Path.Combine(temp, "Local", "DeskKit", "helpers");
        foreach (var v in new[] { "ffmpeg-6.0", "ffmpeg-7.1" })
        {
            Directory.CreateDirectory(Path.Combine(helpers, "ffmpeg", v, "bin"));
            File.WriteAllText(Path.Combine(helpers, "ffmpeg", v, "bin", "ffmpeg.exe"), "");
        }
        Check("helpers: one * matches one folder; the last sorted match wins",
            Runtimes.MatchPath(Path.Combine(helpers, "ffmpeg", "*", "bin", "ffmpeg.exe")) == Path.Combine(helpers, "ffmpeg", "ffmpeg-7.1", "bin", "ffmpeg.exe"));
        Check("helpers: no match gives null", Runtimes.MatchPath(Path.Combine(helpers, "nothing", "*", "x.exe")) == null);

        var dir = Path.Combine(temp, "help");
        var json = Fixture("helped", "oneshot", "hello");
        json["helpers"] = new JsonArray(new JsonObject
        {
            ["id"] = "tool", ["name"] = "Tool", ["required"] = true, ["purpose"] = "Testing",
            ["detect"] = new JsonObject { ["setting"] = "tool_path", ["paths"] = new JsonArray("{helpers_dir}\\tool\\*\\tool.exe"), ["on_path"] = "deskkit-no-such-tool.exe" },
            ["download"] = new JsonObject { ["url"] = "https://example.invalid/tool.zip", ["sha256"] = new string('0', 64), ["archive"] = "zip", ["page"] = "https://example.invalid/" },
            ["licence"] = "MIT",
        });
        WriteModule(dir, "helped", json);
        var e = ModuleScanner.Load(Path.Combine(dir, "helped", "module.json"));
        var h = e.Manifest!.Helpers[0];
        Check("helpers: missing required helper is reported", Runtimes.Diagnose(e)?.StartsWith("Needs Tool (Testing). Get it from https://example.invalid/") == true, Runtimes.Diagnose(e));
        Directory.CreateDirectory(Path.Combine(helpers, "tool", "1.0"));
        File.WriteAllText(Path.Combine(helpers, "tool", "1.0", "tool.exe"), "");
        Check("helpers: {helpers_dir} path is found", Runtimes.DetectHelper(e, h) == Path.Combine(helpers, "tool", "1.0", "tool.exe"));
        var custom = Path.Combine(temp, "custom-tool.exe");
        File.WriteAllText(custom, "");
        var settings = Runtimes.SettingsPath(e)!;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, new JsonObject { ["tool_path"] = custom }.ToJsonString());
        Check("helpers: the settings key wins over paths", Runtimes.DetectHelper(e, h) == custom);
        Check("helpers: diagnosis is clear once found", Runtimes.Diagnose(e) == null, Runtimes.Diagnose(e));
    }

    // ---------------- Autostart ----------------

    private static void TestState(string temp)
    {
        var file = Path.Combine(temp, "state", "state.json");
        var m = new ModuleManifest { Id = "a", RunMode = "tray", Autostart = new AutostartInfo { Default = true } };
        var s = new ShellState(file);
        Check("state: autostart.default applies with no entry", s.IsAutostartOn(m) && !s.HasEntry("a"));
        s.SetAutostart("a", false);
        Check("state: state.json overrides the default", !new ShellState(file).IsAutostartOn(m));
        var root = JsonNode.Parse(File.ReadAllText(file));
        Check("state: file format {modules: {id: {autostart}}}", root?["modules"]?["a"]?["autostart"]?.GetValue<bool>() == false, File.ReadAllText(file));
        File.WriteAllText(file, "not json");
        Check("state: a broken state.json falls back to defaults", new ShellState(file).IsAutostartOn(m));

        Check("login: test Run key in use", LoginEntry.RunKeyPath.StartsWith(@"Software\DeskKit-SelfTest\"));
        Check("login: off at first", !LoginEntry.IsShellOn());
        var cmd = LoginEntry.ShellCommand(@"C:\Apps\DeskKit\DeskKit.exe", @"D:\dev\modules");
        Check("login: command quotes the exe and adds --modules-dir", cmd == "\"C:\\Apps\\DeskKit\\DeskKit.exe\" --modules-dir D:\\dev\\modules", cmd);
        LoginEntry.SetShell(true, cmd);
        Check("login: value DeskKit written", LoginEntry.IsShellOn());
        LoginEntry.SetShell(false, cmd);
        Check("login: value DeskKit removed", !LoginEntry.IsShellOn());

        using (var key = Registry.CurrentUser.CreateSubKey(LoginEntry.RunKeyPath))
            key.SetValue("RegionControl", "\"C:\\X\\RegionControl.exe\"");
        var rc = new ModuleEntry
        {
            Folder = temp, FolderName = "regioncontrol",
            Manifest = new ModuleManifest { Id = "regioncontrol", Entry = "bin/RegionControl.exe", RunMode = "tray", Autostart = new AutostartInfo { ManagedBy = "module" } },
        };
        Check("login: managed_by module reads its own Run entry", LoginEntry.ModuleOwnEntryOn(rc) == true);
        var c = new ModuleController(Path.Combine(temp, "none"), new ShellState(file));
        Check("login: managed_by module is not editable", !c.AutostartEditable(rc) && c.AutostartText(rc) == "on (own entry)", c.AutostartText(rc));
    }

    private static void TestLogs(string temp)
    {
        var log = Path.Combine(temp, "logs", "x.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, new string('x', (int)Launcher.MaxLogBytes + 10));
        Launcher.WriteStartLine(log, new[] { "a", "b" });
        Check("log: rotates to .log.1 past 1 MB", File.Exists(log + ".1") && new FileInfo(log).Length < 200);
        var line = File.ReadAllText(log);
        Check("log: start line format", line.StartsWith("=== ") && line.TrimEnd().EndsWith(" start a b ==="), line);
    }

    // ---------------- Real processes ----------------

    private static async Task TestProcesses(string temp, string fixture)
    {
        var dir = Path.Combine(temp, "proc");
        WriteModule(dir, "st-hello", Fixture("st-hello", "oneshot", "hello", "a b"), fixture);
        WriteModule(dir, "st-window", Fixture("st-window", "window", "window"), fixture);
        WriteModule(dir, "st-stubborn", Fixture("st-stubborn", "window", "stubborn"), fixture);
        WriteModule(dir, "st-crash", Fixture("st-crash", "window", "crash"), fixture);
        WriteModule(dir, "st-sleep", Fixture("st-sleep", "oneshot", "sleep"), fixture);
        var foreignJson = Fixture("st-foreign", "window", "window");
        foreignJson["instance"]!["stop_event"] = null;   // like a pinned module: no stop event
        WriteModule(dir, "st-foreign", foreignJson, fixture);

        var notices = new List<string>();
        using var c = new ModuleController(dir, ShellState.Open()) { OneshotTimeout = TimeSpan.FromSeconds(3) };
        c.Notify += (t, text, _) => { lock (notices) notices.Add(text); };
        ModuleEntry E(string id) => c.Find(id) ?? throw new InvalidOperationException(id + " not found");

        // Oneshot: args, environment and log capture.
        var err = c.Start(E("st-hello"));
        Check("start: oneshot starts", err == null, err);
        await WaitFor(() => !c.OwnsLiveProcess("st-hello"), 20);
        var helloLog = ReadShared(Launcher.LogFile("st-hello"));
        Check("start: log has the start line with args", helloLog.Contains(" start hello a b ==="), helloLog);
        Check("start: args arrive as separate elements", helloLog.Contains("arg1=hello") && helloLog.Contains("arg2=a b"), helloLog);
        Check("start: DESKKIT_SHELL=1 is set", helloLog.Contains("DESKKIT_SHELL=1"), helloLog);
        Check("start: DESKKIT_DATA_DIR is the expanded data dir",
            helloLog.Contains("DATA=" + Path.Combine(temp, "Roaming", "DeskKit", "st-hello")), helloLog);
        Check("start: DESKKIT_MODULE_DIR is the module dir", helloLog.Contains("MODULE_DIR=" + Path.Combine(dir, "st-hello")), helloLog);
        Check("start: unknown action is refused", c.Start(E("st-hello"), "nope") != null);

        // Window module: start, running state, stop with its stop event.
        var w = E("st-window");
        Check("window: starts", c.Start(w) == null);
        Check("window: mutex appears", await WaitFor(() => Native.MutexExists(@"Local\DeskKit.st-window"), 20));
        Check("window: state is running", c.IsRunning(w) && c.StateText(w) == "running", c.StateText(w));
        Check("window: a second start hands over and exits 0", c.Start(w) == null);
        await Task.Delay(3000);
        var stopMsg = await c.StopAsync(w);
        Check("window: stop event stops it (step 1)", stopMsg == null && !Native.MutexExists(@"Local\DeskKit.st-window"), stopMsg);
        await WaitFor(() => ReadShared(Launcher.LogFile("st-window")).Contains("stop received"), 5);
        var wlog = ReadShared(Launcher.LogFile("st-window"));
        Check("window: module saw the stop event", wlog.Contains("stop received"), wlog);
        Check("window: second copy handed over to the first", wlog.Contains("second copy: handed over") && wlog.Contains("show received"), wlog);
        Check("window: state is stopped", !c.IsRunning(w));

        // A copy the shell did not start, with a stop event: step 1 still works (approved reading of 7.3).
        using (var p = StartForeign(fixture, "st-window"))
        {
            await WaitFor(() => Native.MutexExists(@"Local\DeskKit.st-window"), 20);
            Check("foreign: state says not started by DeskKit", c.StateText(w) == "running (not started by DeskKit)", c.StateText(w));
            var m1 = await c.StopAsync(w);
            Check("foreign: its own stop event stops it", m1 == null && p.WaitForExit(5000), m1);
        }

        // A copy the shell did not start, with no stop event: the shell never kills it.
        using (var p = StartForeign(fixture, "st-foreign"))
        {
            var f = E("st-foreign");
            await WaitFor(() => Native.MutexExists(@"Local\DeskKit.st-foreign"), 20);
            var m2 = await c.StopAsync(f);
            Check("foreign: no stop event gives the not-started message", m2 == RunModes.NotStartedByShellMessage("Hello st-foreign"), m2);
            Check("foreign: the shell did not kill it", !p.HasExited);
            try { p.Kill(true); } catch (Exception) { }   // the test started it, so the test stops it
            p.WaitForExit(5000);
        }

        // Stubborn module: stop event ignored, no windows, so the shell kills its own process tree.
        var s = E("st-stubborn");
        c.Start(s);
        await WaitFor(() => Native.MutexExists(@"Local\DeskKit.st-stubborn"), 20);
        var pid = c.OwnProcessId("st-stubborn");
        var sw = Stopwatch.StartNew();
        var m3 = await c.StopAsync(s);
        Check("stubborn: stop falls through to kill (step 3)", m3 == null && !Native.MutexExists(@"Local\DeskKit.st-stubborn"), m3);
        Check("stubborn: its process is gone", pid != null && !ProcessAlive(pid.Value));
        Check("stubborn: step 1 waited about 5 s first", sw.Elapsed.TotalSeconds >= 4.5, sw.Elapsed.ToString());

        // Crash notice with the last ERROR line.
        c.Start(E("st-crash"));
        Check("crash: notice shows the ERROR line",
            await WaitFor(() => { lock (notices) return notices.Any(n => n.Contains("ERROR: fixture crashed on purpose")); }, 20),
            string.Join(" / ", notices));

        // Oneshot time limit (3 s in the test, 60 s in the shell).
        c.Start(E("st-sleep"));
        var sleepPid = c.OwnProcessId("st-sleep");
        Check("oneshot: killed after its time limit with a notice",
            await WaitFor(() => { lock (notices) return notices.Contains(RunModes.OneshotTimeoutMessage("Hello st-sleep")); }, 20)
            && sleepPid != null && await WaitFor(() => !ProcessAlive(sleepPid.Value), 5), string.Join(" / ", notices));
        Check("stopped modules raise no crash notice", !notices.Any(n => n.Contains("st-stubborn") || n.Contains("st-window")), string.Join(" / ", notices));
    }

    /// <summary>--run id -- args through the real RunOnce path, into a module that echoes its arguments.</summary>
    private static async Task TestPassThrough(string temp, string echo)
    {
        var dir = Path.Combine(temp, "echo");
        var json = Fixture("st-echo", "oneshot", "fixed", "{data_dir}");
        WriteModule(dir, "st-echo", json, echo);
        var extra = new[] { "Tools", "two words", "x\"y", @"C:\path with space\", "-flag", "{data_dir}", "%APPDATA%", "", "--", "last" };
        var log = Launcher.LogFile("st-echo");

        var code = Program.RunOnce(dir, "st-echo", null, extra);
        Check("pass-through: --run st-echo -- ... exits 0", code == 0, code.ToString());
        Check("pass-through: the module printed all its args", await WaitFor(() => ReadShared(log).Contains("argc="), 20), ReadShared(log));
        var text = ReadShared(log);
        var expected = new List<string> { "fixed", Path.Combine(temp, "Roaming", "DeskKit", "st-echo") };
        expected.AddRange(extra);
        var got = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("argv[")).Select(l => l[(l.IndexOf("=<") + 2)..^1]).ToList();
        Check("pass-through: args then extra args arrive in order, each one exact", got.SequenceEqual(expected),
            "got: " + string.Join(" | ", got));
        Check("pass-through: the log start line lists the extra args", text.Contains(" start fixed {data_dir} Tools two words"), text);

        File.Delete(log);
        code = Program.RunOnce(dir, "st-echo", null, null);
        await WaitFor(() => ReadShared(log).Contains("argc="), 20);
        Check("pass-through: without -- only the module's args arrive", code == 0 && ReadShared(log).Contains("argc=2"), ReadShared(log));
        Check("--run: unknown id exits 1", Program.RunOnce(dir, "nosuch", null, null) == 1);
        Check("--run: unknown action exits 1", Program.RunOnce(dir, "st-echo", "nosuch", null) == 1);
    }

    // ---------------- Utilities ----------------

    private static JsonObject Fixture(string id, string mode, params string[] args)
    {
        bool longRunning = RunModes.IsLongRunning(mode);
        return new JsonObject
        {
            ["schema_version"] = 1, ["id"] = id, ["name"] = "Hello " + id, ["version"] = "0.0.1",
            ["description"] = "Self-test module.", ["source"] = "in-repo", ["repo"] = null,
            ["runtime"] = new JsonObject { ["type"] = "powershell", ["min"] = "5.1" },
            ["entry"] = "src/fixture.ps1", ["args"] = new JsonArray(args.Select(a => (JsonNode?)a).ToArray()),
            ["console"] = false, ["run_mode"] = mode,
            ["instance"] = new JsonObject
            {
                ["single"] = longRunning,
                ["mutex"] = longRunning ? $"Local\\DeskKit.{id}" : null,
                ["show_event"] = longRunning ? $"Local\\DeskKit.{id}.show" : null,
                ["stop_event"] = longRunning ? $"Local\\DeskKit.{id}.stop" : null,
            },
            ["actions"] = new JsonArray(), ["autostart"] = new JsonObject { ["default"] = false, ["managed_by"] = "shell" },
            ["icon"] = null, ["data_dir"] = $"%APPDATA%\\DeskKit\\{id}", ["settings_path"] = "{data_dir}\\settings.json",
            ["settings_action"] = null, ["launcher"] = null, ["helpers"] = new JsonArray(), ["licence"] = "MIT", ["homepage"] = null,
        };
    }

    private static void WriteModule(string modulesDir, string folder, JsonObject json, string? script = null)
    {
        var d = Path.Combine(modulesDir, folder);
        Directory.CreateDirectory(Path.Combine(d, "src"));
        File.WriteAllText(Path.Combine(d, "module.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (script != null) File.Copy(script, Path.Combine(d, "src", "fixture.ps1"), overwrite: true);
        else File.WriteAllText(Path.Combine(d, "src", "fixture.ps1"), "exit 0");
    }

    private static Process StartForeign(string fixture, string id)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-NonInteractive", "-File", fixture, "window" }) psi.ArgumentList.Add(a);
        psi.Environment["DESKKIT_MODULE_ID"] = id;
        psi.Environment.Remove("DESKKIT_SHELL");
        return Process.Start(psi)!;
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return condition();
    }

    private static string ReadShared(string file)
    {
        if (!File.Exists(file)) return "";
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd();
    }
}
