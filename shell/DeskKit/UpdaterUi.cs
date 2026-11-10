// Update checks for the tray and the main window (contract 16.7, 16.9): first check 30 s after start, then
// every 24 h when automatic checks are on; one notice per new version; Update per module; the shell's own
// update through the suite installer; notify-only rows open the release page.
namespace DeskKit.Shell;

public sealed class UpdateManager : IDisposable
{
    private readonly TrayApp _app;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Dictionary<string, UpdateCheck> _checks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _status = new(StringComparer.Ordinal);
    private bool _busy;
    private DateTimeOffset? _lastCheck;
    private string? _lastError;

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public UpdateManager(TrayApp app)
    {
        _app = app;
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = (int)Interval.TotalMilliseconds;
            if (UpdaterState.CheckEnabled) Check(manual: false);
        };
    }

    /// <summary>Seconds before the first automatic check: 30, or DESKKIT_UPDATE_FIRST_DELAY (tests).</summary>
    public static int FirstDelaySeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("DESKKIT_UPDATE_FIRST_DELAY"), out var s) && s >= 0 ? s : 30;

    public void Start()
    {
        UpdateInstaller.CleanLeftovers(_app.Controller.ModulesDir);
        _timer.Interval = Math.Max(1, FirstDelaySeconds * 1000);
        _timer.Start();
    }

    public bool Busy => _busy;

    // ---------------- Check ----------------

    public async void Check(bool manual)
    {
        if (_busy) return;
        _busy = true;
        _app.RefreshProblems();
        List<UpdateCheck> checks;
        try
        {
            var modulesDir = _app.Controller.ModulesDir;
            var modules = _app.Controller.Modules.ToList();
            checks = await Task.Run(() => UpdateCli.CheckAllAsync(modulesDir, modules));
        }
        catch (Exception ex)
        {
            ShellLog.Write($"updates: check failed: {ex}");
            _busy = false;
            if (manual) _app.ShowNotice("DeskKit", $"Could not check for updates: {ex.Message}", true);
            _app.RefreshProblems();
            return;
        }
        _busy = false;
        _lastCheck = DateTimeOffset.Now;
        _checks.Clear();
        foreach (var c in checks) _checks[c.Component.Id] = c;
        var errors = checks.Where(c => c.State == UpdateStateKind.Error).Select(c => c.Note).Distinct().ToList();
        _lastError = errors.Count > 0 ? errors[0] : null;

        var found = checks.Where(c => c.HasUpdate).ToList();
        if (manual)
        {
            if (found.Count > 0) _app.ShowNotice("DeskKit", "Updates available: " + Names(found), false);
            else if (_lastError != null) _app.ShowNotice("DeskKit", $"Could not check for updates: {_lastError}", true);
            else _app.ShowNotice("DeskKit", "Everything is up to date.", false);
            foreach (var c in found) UpdaterState.SetNotified(c.Component.Id, c.Available!);
        }
        else
        {
            var fresh = found.Where(c => UpdaterState.Notified(c.Component.Id) != c.Available).ToList();
            if (fresh.Count > 0)
            {
                _app.ShowNotice("DeskKit", "Updates available: " + Names(fresh), false);
                foreach (var c in fresh) UpdaterState.SetNotified(c.Component.Id, c.Available!);
            }
        }
        _app.RefreshProblems();
    }

    private static string Names(IEnumerable<UpdateCheck> list) =>
        string.Join(", ", list.Select(c => $"{c.Component.Name} {c.Available}"));

    // ---------------- State for the window ----------------

    public UpdateCheck? CheckFor(string id) => _checks.TryGetValue(id, out var c) ? c : null;

    public bool CanUpdate(ModuleEntry e) =>
        !_busy && CheckFor(e.Id) is { State: UpdateStateKind.Update } c && c.Component.Entry != null;

    /// <summary>The status line of a module: the last update result, else the last check.</summary>
    public string Status(ModuleEntry e)
    {
        if (_busy) return "Checking for updates...";
        if (_status.TryGetValue(e.Id, out var s)) return s;
        if (_lastCheck == null) return UpdaterState.CheckEnabled ? "Updates: not checked yet." : "Updates: automatic checks are off.";
        var when = _lastCheck.Value.ToString("HH:mm");
        return CheckFor(e.Id) switch
        {
            null => $"Updates: not checked (checked {when}).",
            { State: UpdateStateKind.Update } c => $"Update available: {c.Available} (installed {c.Component.InstalledVersion}). Click Update.",
            { State: UpdateStateKind.NotifyOnly } c => $"Update available: {c.Available}. {c.Note}",
            { State: UpdateStateKind.Current } => $"Up to date (checked {when}).",
            { State: UpdateStateKind.NoSource } c => $"Updates: {c.Note}.",
            var c => $"Could not check for updates: {c.Note}",
        };
    }

    // ---------------- Update ----------------

    public async void Update(ModuleEntry e)
    {
        if (_busy || CheckFor(e.Id) is not { } c) return;
        _busy = true;
        _status[e.Id] = $"Updating {e.DisplayName} to {c.Available}...";
        _app.RefreshProblems();
        UpdateOutcome o;
        try
        {
            o = await UpdateInstaller.UpdateModuleAsync(c, _app.Controller, Ask);
        }
        catch (Exception ex)
        {
            ShellLog.Write($"update {e.Id} failed: {ex}");
            o = new UpdateOutcome(UpdateResult.Failed, $"{e.DisplayName} could not be updated: {ex.Message}");
        }
        _busy = false;
        _status[e.Id] = o.Message;
        if (o.Result == UpdateResult.Updated) _checks.Remove(e.Id);
        _app.Reload();
        _app.ShowNotice(e.DisplayName, o.Message, o.Result is UpdateResult.Failed or UpdateResult.RolledBack);
    }

    private static bool Ask(string question)
    {
        if (Environment.GetEnvironmentVariable("DESKKIT_NO_UI") == "1")
        {
            ShellLog.Write($"update: \"{question}\" -> No (DESKKIT_NO_UI=1)");
            return false;
        }
        return MessageBox.Show(question, "DeskKit", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    public async void UpdateShell()
    {
        if (_busy || CheckFor("shell") is not { State: UpdateStateKind.Update } c) return;
        if (Environment.GetEnvironmentVariable("DESKKIT_NO_UI") != "1" &&
            MessageBox.Show($"Download DeskKit {c.Available} and run its installer? DeskKit closes while it installs; your modules keep running.",
                "DeskKit", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _busy = true;
        UpdateOutcome o;
        try { o = await UpdateInstaller.UpdateShellAsync(c); }
        catch (Exception ex) { o = new UpdateOutcome(UpdateResult.Failed, ex.Message); }
        _busy = false;
        _app.ShowNotice("DeskKit", o.Message, o.Result == UpdateResult.Failed);
        if (o.Result == UpdateResult.Started && Environment.GetEnvironmentVariable("DESKKIT_UPDATE_NO_RUN") != "1") _app.ExitApp();
    }

    // ---------------- Tray menu ----------------

    public void AddMenuItems(ToolStripItemCollection items)
    {
        if (CheckFor("shell") is { State: UpdateStateKind.Update } sc)
            items.Add(new ToolStripMenuItem($"Update DeskKit to {sc.Available}...", null, (_, _) => UpdateShell()) { Name = "mnuUpdateShell" });
        foreach (var c in _checks.Values.Where(c => c.State == UpdateStateKind.NotifyOnly && c.Release != null))
        {
            var url = c.Release!.HtmlUrl;
            items.Add(new ToolStripMenuItem($"{c.Component.Name} {c.Available}: open release page", null,
                (_, _) => { try { ModuleController.ShellOpen(url); } catch (Exception ex) { _app.ShowNotice("DeskKit", ex.Message, true); } }));
        }
        items.Add(new ToolStripMenuItem(_busy ? "Checking for updates..." : "Check for updates", null, (_, _) => Check(manual: true))
            { Name = "mnuCheckUpdates", Enabled = !_busy });
        items.Add(new ToolStripMenuItem("Check for updates automatically", null, (_, _) =>
        {
            UpdaterState.CheckEnabled = !UpdaterState.CheckEnabled;
            ShellLog.Write($"updates: automatic checks {(UpdaterState.CheckEnabled ? "on" : "off")}");
        }) { Name = "mnuAutoUpdates", Checked = UpdaterState.CheckEnabled });
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
