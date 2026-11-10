// --check-updates and --update <id|all|shell> [--yes] (contract 16.9).
namespace DeskKit.Shell;

public static class UpdateCli
{
    public static async Task<List<UpdateCheck>> CheckAllAsync(string modulesDir, IEnumerable<ModuleEntry> modules, CancellationToken ct = default)
    {
        UpdateInstaller.CleanLeftovers(modulesDir);
        var comps = UpdateSources.Components(modulesDir, modules);
        var checks = await UpdateChecker.CheckAsync(comps, UpdaterState.IncludePrereleases, null, ct);
        UpdaterState.LastCheck = DateTimeOffset.Now;
        return checks;
    }

    public static string Table(IEnumerable<UpdateCheck> checks)
    {
        var rows = new List<string[]> { new[] { "ID", "INSTALLED", "AVAILABLE", "STATE", "NOTE" } };
        foreach (var c in checks)
            rows.Add(new[] { c.Component.Id, c.Component.InstalledVersion, c.Available ?? "-", UpdateChecker.StateWord(c.State), c.Note });
        var widths = Enumerable.Range(0, 4).Select(i => rows.Max(r => r[i].Length)).ToArray();
        var sb = new System.Text.StringBuilder();
        foreach (var r in rows)
        {
            for (int i = 0; i < 4; i++) sb.Append(r[i].PadRight(widths[i] + 2));
            sb.AppendLine(r[4]);
        }
        return sb.ToString();
    }

    /// <summary>--check-updates: print the table, install nothing, exit 0 (also offline).</summary>
    public static int Check(string modulesDir)
    {
        var checks = CheckAllAsync(modulesDir, ModuleScanner.Scan(modulesDir)).GetAwaiter().GetResult();
        Console.WriteLine($"DeskKit {Program.Version}: update check, feed {UpdateSources.FeedBase}");
        Console.Write(Table(checks));
        return 0;
    }

    /// <summary>--update: 0 updated or current, 1 a failure, 3 skipped (declined, busy, notify only).</summary>
    public static int Update(string modulesDir, string target, bool yes)
    {
        using var controller = new ModuleController(modulesDir, ShellState.Open());
        var checks = CheckAllAsync(modulesDir, controller.Modules).GetAwaiter().GetResult();
        bool noUi = Environment.GetEnvironmentVariable("DESKKIT_NO_UI") == "1";
        bool Ask(string question)
        {
            if (yes) return true;
            if (noUi) { Console.WriteLine($"{question} -> No (DESKKIT_NO_UI=1 and no --yes)"); return false; }
            return MessageBox.Show(question, "DeskKit", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        if (target == "shell")
        {
            var sc = checks.First(c => c.Component.Kind == ComponentKind.Shell);
            if (sc.State == UpdateStateKind.Error || sc.State == UpdateStateKind.NoSource)
                return Report("shell", new UpdateOutcome(UpdateResult.Failed, $"Could not check for updates: {sc.Note}"));
            return Report("shell", UpdateInstaller.UpdateShellAsync(sc).GetAwaiter().GetResult());
        }

        List<UpdateCheck> picked;
        if (target == "all")
            picked = checks.Where(c => c.Component.Kind != ComponentKind.Shell && c.HasUpdate).ToList();
        else
        {
            var one = checks.FirstOrDefault(c => c.Component.Id == target && c.Component.Kind != ComponentKind.Shell);
            if (one == null)
            {
                Console.Error.WriteLine($"ERROR: DeskKit has no module \"{target}\" in {modulesDir}.");
                return 2;
            }
            if (one.State is UpdateStateKind.Error or UpdateStateKind.NoSource)
                return Report(target, new UpdateOutcome(UpdateResult.Failed, $"Could not check for updates: {one.Note}"));
            picked = new() { one };
        }

        if (picked.Count == 0) { Console.WriteLine("All modules are up to date."); return 0; }
        int code = 0;
        foreach (var c in picked)
        {
            var o = UpdateInstaller.UpdateModuleAsync(c, controller, Ask).GetAwaiter().GetResult();
            var r = Report(c.Component.Id, o);
            if (r == 1) code = 1;
            else if (r == 3 && code == 0 && !(target == "all" && o.Result == UpdateResult.NotifyOnly)) code = 3;
        }
        return code;
    }

    private static int Report(string id, UpdateOutcome o)
    {
        var line = $"{id}: {o.Word}: {o.Message}";
        if (o.Result is UpdateResult.Failed or UpdateResult.RolledBack) Console.Error.WriteLine("ERROR: " + line);
        else Console.WriteLine(line);
        return o.Result switch
        {
            UpdateResult.Updated or UpdateResult.Current or UpdateResult.Started => 0,
            UpdateResult.Declined or UpdateResult.NotifyOnly => 3,
            _ => 1,
        };
    }
}
