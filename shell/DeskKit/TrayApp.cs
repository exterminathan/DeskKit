// The tray app: one NotifyIcon with a menu per module, the main window, a 2 s state check (contract 7.4),
// autostart at its own start, and two named events so a second copy can show or close this one.
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DeskKit.Shell;

public sealed class TrayApp : ApplicationContext
{
    public const string ShowEventName = @"Local\DeskKit.Shell.show";
    public const string ExitEventName = @"Local\DeskKit.Shell.exit";

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly Control _ui = new();          // marshals work onto the UI thread
    private readonly EventWaitHandle _showEvent, _exitEvent;
    private readonly RegisteredWaitHandle _showWait, _exitWait;
    private readonly string? _modulesDirArg;
    private MainForm? _form;
    private bool _exiting;

    public ModuleController Controller { get; }
    public UpdateManager Updates { get; }
    public Dictionary<string, string?> Problems { get; } = new();
    public Icon AppIcon { get; } = AppIcons.Create();

    public TrayApp(ModuleController controller, string? modulesDirArg, bool showWindow)
    {
        Controller = controller;
        _modulesDirArg = modulesDirArg;
        Updates = new UpdateManager(this);
        _ui.CreateControl();
        _ = _ui.Handle;

        _icon = new NotifyIcon { Icon = AppIcon, Text = "DeskKit", ContextMenuStrip = _menu, Visible = true };
        ShellLog.Write($"tray icon created (modules dir {controller.ModulesDir})");
        _menu.Opening += (_, _) => BuildMenu();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMainWindow(); };
        _icon.BalloonTipClicked += (_, _) => ShowMainWindow();

        Controller.Notify += (title, text, error) => OnUi(() => ShowNotice(title, text, error));

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => OnUi(ShowMainWindow), null, -1, false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => OnUi(ExitApp), null, -1, true);

        _timer.Tick += (_, _) => _form?.RefreshStates();
        _timer.Start();

        // After the icon shows: diagnose (finding Python takes a moment), then autostart, then the window.
        OnUi(() =>
        {
            RefreshProblems();
            Controller.StartAutostartModules();
            Updates.Start();   // first update check after 30 s, then every 24 h (contract 16.7)
            if (showWindow) ShowMainWindow();
        });
    }

    public void OnUi(Action a)
    {
        if (_exiting || _ui.IsDisposed) return;
        try { _ui.BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    /// <summary>Rescan modules\ and recompute each module's problem line.</summary>
    public void Reload()
    {
        Controller.Reload();
        RefreshProblems();
    }

    public void RefreshProblems()
    {
        Problems.Clear();
        foreach (var e in Controller.Modules) Problems[e.Id] = Runtimes.Diagnose(e);
        _form?.RebuildList();
    }

    public string? Problem(ModuleEntry e) => Problems.TryGetValue(e.Id, out var p) ? p : null;

    // ---------------- Menu ----------------

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var open = new ToolStripMenuItem("Open DeskKit", null, (_, _) => ShowMainWindow()) { Font = new Font(_menu.Font, FontStyle.Bold) };
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripSeparator());
        if (Controller.Modules.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem($"No modules in {Controller.ModulesDir}") { Enabled = false });
        foreach (var e in Controller.Modules)
        {
            var running = Controller.IsRunning(e);
            var item = new ToolStripMenuItem(e.DisplayName + (running ? "  (running)" : "")) { Image = ModuleIcons.Get(e) };
            if (e.IsBroken || Problem(e) != null) item.ForeColor = Color.Firebrick;
            ModuleMenu.Fill(item.DropDownItems, e, this);
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());
        Updates.AddMenuItems(_menu.Items);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Start DeskKit at login", null, (_, _) => SetShellLogin(!LoginEntry.IsShellOn()))
            { Checked = LoginEntry.IsShellOn() });
        _menu.Items.Add(new ToolStripMenuItem("Rescan modules", null, (_, _) => Reload()));
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));
    }

    // ---------------- Commands used by the menu and the window ----------------

    public async void Run(Func<Task<string?>> work)
    {
        try
        {
            var msg = await work();
            if (msg != null) ShowNotice("DeskKit", msg, true);
        }
        catch (Exception ex)
        {
            ShellLog.Write($"command failed: {ex}");
            ShowNotice("DeskKit", ex.Message, true);
        }
        _form?.RefreshStates();
    }

    public void Toggle(ModuleEntry e) => Run(() => Controller.ToggleAsync(e));
    public void Stop(ModuleEntry e) => Run(() => Controller.StopAsync(e));
    public void StartAction(ModuleEntry e, string actionId) => Run(() => Task.FromResult(Controller.Start(e, actionId)));
    public void OpenSettings(ModuleEntry e) => Run(() => Task.FromResult(Controller.OpenSettings(e)));
    public void OpenSettingsFolder(ModuleEntry e) => Run(() => Task.FromResult(Controller.OpenSettingsFolder(e)));
    public void OpenLog(ModuleEntry e) => Run(() => Task.FromResult(Controller.OpenLog(e)));

    public void SetAutostart(ModuleEntry e, bool on)
    {
        Controller.State.SetAutostart(e.Id, on);
        ShellLog.Write($"autostart {e.Id} set {(on ? "on" : "off")}");
        if (on && !LoginEntry.IsShellOn())
        {
            SetShellLogin(true);
            ShowNotice("DeskKit", $"DeskKit now starts at login, so it can start {e.DisplayName}.", false);
        }
        _form?.RebuildList();
    }

    public void SetShellLogin(bool on)
    {
        try
        {
            LoginEntry.SetShell(on, LoginEntry.ShellCommand(Environment.ProcessPath!, _modulesDirArg));
            ShellLog.Write($"shell login entry {(on ? "on" : "off")}");
        }
        catch (Exception ex) { ShowNotice("DeskKit", $"Cannot change the login entry: {ex.Message}", true); }
        _form?.RefreshShellLogin();
    }

    public void DownloadHelper(ModuleEntry e, HelperInfo h)
    {
        if (MessageBox.Show(HelperDownloader.ConfirmText(h), "DeskKit", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        ShowNotice("DeskKit", $"Downloading {h.Name}...", false);
        Run(async () =>
        {
            var msg = await HelperDownloader.DownloadAsync(e, h);
            RefreshProblems();
            ShowNotice("DeskKit", msg, false);
            return null;
        });
    }

    public void ShowNotice(string title, string text, bool error)
    {
        ShellLog.Write($"notice: {title}: {text}");
        _icon.ShowBalloonTip(8000, title, text.Length > 250 ? text[..250] + "..." : text, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    public void ShowMainWindow()
    {
        if (_form == null || _form.IsDisposed) _form = new MainForm(this);
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    public bool Exiting => _exiting;

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        ShellLog.Write("shell exit (modules keep running)");
        _timer.Stop();
        Updates.Dispose();
        _showWait.Unregister(null);
        _exitWait.Unregister(null);
        _icon.Visible = false;
        _icon.Dispose();
        _form?.Close();
        Controller.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showEvent.Dispose();
            _exitEvent.Dispose();
            _timer.Dispose();
            _menu.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The per-module items, shared by the tray submenu and the main window's right-click menu.</summary>
public static class ModuleMenu
{
    public static void Fill(ToolStripItemCollection items, ModuleEntry e, TrayApp app)
    {
        var c = app.Controller;
        if (e.IsBroken)
        {
            items.Add(new ToolStripMenuItem($"Broken: {e.Error}") { Enabled = false });
            return;
        }
        var m = e.Manifest!;
        var running = c.IsRunning(e);
        items.Add(new ToolStripMenuItem($"{m.Name} {m.Version}: {c.StateText(e)}") { Enabled = false });
        if (app.Problem(e) is { } problem)
            items.Add(new ToolStripMenuItem(problem) { Enabled = false, ForeColor = Color.Firebrick });
        items.Add(new ToolStripSeparator());

        items.Add(new ToolStripMenuItem(RunModes.MainControlLabel(m.RunMode, running), null, (_, _) => app.Toggle(e)));
        if (RunModes.StopOffered(m.RunMode))
            items.Add(new ToolStripMenuItem("Stop", null, (_, _) => app.Stop(e))
                { Enabled = running || c.OwnsLiveProcess(m.Id) });   // if it cannot stop it, Stop says why
        foreach (var a in m.Actions.Where(a => a.Id != m.SettingsAction))
            items.Add(new ToolStripMenuItem(a.Label, null, (_, _) => app.StartAction(e, a.Id)));

        items.Add(new ToolStripSeparator());
        if (ModuleController.HasSettings(e))
            items.Add(new ToolStripMenuItem("Settings", null, (_, _) => app.OpenSettings(e)));
        items.Add(new ToolStripMenuItem("Open settings folder", null, (_, _) => app.OpenSettingsFolder(e)));
        items.Add(new ToolStripMenuItem("View log", null, (_, _) => app.OpenLog(e)));
        foreach (var h in m.Helpers.Where(h => Runtimes.DetectHelper(e, h) == null))
            items.Add(new ToolStripMenuItem($"Download {h.Name}...", null, (_, _) => app.DownloadHelper(e, h)));

        if (c.AutostartEditable(e))
        {
            var on = c.IsAutostartOn(e);
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem("Start at login", null, (_, _) => app.SetAutostart(e, !on)) { Checked = on });
        }
        else if (RunModes.AutostartAllowed(m.RunMode))
        {
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem($"Start at login: {c.AutostartText(e)}") { Enabled = false });
        }
    }
}

/// <summary>The DeskKit icon, drawn in code (no image files to ship).</summary>
public static class AppIcons
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(1, 1, r * 2, r * 2, 180, 90);
            path.AddArc(30 - r * 2, 1, r * 2, r * 2, 270, 90);
            path.AddArc(30 - r * 2, 30 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(1, 30 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(0x2B, 0x6C, 0xB0));
            g.FillPath(fill, path);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, 8, 8, 7, 7);
            g.FillRectangle(white, 17, 8, 7, 7);
            g.FillRectangle(white, 8, 17, 7, 7);
            using var accent = new SolidBrush(Color.FromArgb(0xF6, 0xC1, 0x45));
            g.FillRectangle(accent, 17, 17, 7, 7);
        }
        var h = bmp.GetHicon();
        return (Icon)Icon.FromHandle(h).Clone();
    }
}

/// <summary>Module icons for menus: icon.ico, else the exe's own icon (dotnet, native), else none.</summary>
public static class ModuleIcons
{
    private static readonly Dictionary<string, Image?> Cache = new();

    public static Image? Get(ModuleEntry e)
    {
        if (Cache.TryGetValue(e.Folder, out var img)) return img;
        img = null;
        try
        {
            if (e.Manifest is { } m)
            {
                string? file = m.Icon != null ? Path.Combine(e.Folder, m.Icon.Replace('/', '\\')) : null;
                if (file != null && File.Exists(file))
                {
                    using var ico = new Icon(file, 16, 16);
                    img = ico.ToBitmap();
                }
                else if (m.Runtime.Type is "dotnet" or "native" && File.Exists(Runtimes.EntryPath(e)))
                {
                    using var ico = Icon.ExtractAssociatedIcon(Runtimes.EntryPath(e));
                    if (ico != null) img = new Bitmap(ico.ToBitmap(), 16, 16);
                }
            }
        }
        catch (Exception) { img = null; }
        Cache[e.Folder] = img;
        return img;
    }
}
