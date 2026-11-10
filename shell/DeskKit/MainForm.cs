// The main window: the module list with state, version and a clear problem line, and buttons for the
// selected module. Closing the window hides it; the tray keeps running.
using System.Drawing;

namespace DeskKit.Shell;

public sealed class MainForm : Form
{
    private readonly TrayApp _app;
    private readonly ListView _list = new()
    {
        Name = "moduleList", View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, Dock = DockStyle.Fill, GridLines = false,
    };
    private readonly Label _title = new() { Name = "lblTitle", AutoSize = false, Dock = DockStyle.Top, Height = 22, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Label _desc = new() { Name = "lblDescription", AutoSize = false, Dock = DockStyle.Top, Height = 20 };
    private readonly Label _problem = new() { Name = "lblProblem", AutoSize = false, Dock = DockStyle.Top, Height = 36, ForeColor = Color.Firebrick };
    private readonly Button _main = new() { Name = "btnMain", Text = "Start", AutoSize = true };
    private readonly Button _stop = new() { Name = "btnStop", Text = "Stop", AutoSize = true };
    private readonly Button _actions = new() { Name = "btnActions", Text = "Actions ▾", AutoSize = true };
    private readonly Button _settings = new() { Name = "btnSettings", Text = "Settings", AutoSize = true };
    private readonly Button _folder = new() { Name = "btnFolder", Text = "Settings folder", AutoSize = true };
    private readonly Button _log = new() { Name = "btnLog", Text = "Log", AutoSize = true };
    private readonly Button _update = new() { Name = "btnUpdate", Text = "Update", AutoSize = true };
    private readonly Label _updateStatus = new() { Name = "lblUpdateStatus", AutoSize = false, Dock = DockStyle.Top, Height = 20, ForeColor = SystemColors.GrayText };
    private readonly Button _checkUpdates = new() { Name = "btnCheckUpdates", Text = "Check for updates", AutoSize = true };
    private readonly CheckBox _autostart = new() { Name = "chkAutostart", Text = "Start at login", AutoSize = true };
    private readonly CheckBox _shellLogin = new() { Name = "chkShellLogin", Text = "Start DeskKit at login", AutoSize = true };
    private readonly Button _rescan = new() { Name = "btnRescan", Text = "Rescan", AutoSize = true };
    private readonly Button _exit = new() { Name = "btnExit", Text = "Exit DeskKit", AutoSize = true };
    private readonly Label _dir = new() { Name = "lblModulesDir", AutoSize = true, ForeColor = SystemColors.GrayText };
    private bool _updating;

    public MainForm(TrayApp app)
    {
        _app = app;
        Text = "DeskKit";
        Icon = app.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 460);
        MinimumSize = new Size(640, 340);
        Font = SystemFonts.MessageBoxFont ?? Font;

        _list.Columns.Add("Module", 170);
        _list.Columns.Add("Version", 70);
        _list.Columns.Add("Mode", 70);
        _list.Columns.Add("State", 170);
        _list.Columns.Add("Start at login", 110);
        _list.Columns.Add("Status", 360);
        _list.SelectedIndexChanged += (_, _) => UpdateDetails();
        _list.DoubleClick += (_, _) => { if (Selected() is { } e) _app.Toggle(e); };
        _list.MouseUp += (_, ev) =>
        {
            if (ev.Button != MouseButtons.Right || Selected() is not { } e) return;
            var menu = new ContextMenuStrip();
            ModuleMenu.Fill(menu.Items, e, _app);
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(_list, ev.Location);
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 4, 0, 4) };
        buttons.Controls.AddRange(new Control[] { _main, _stop, _actions, _settings, _folder, _log, _update, _autostart });
        _autostart.Margin = new Padding(12, 7, 3, 3);

        var details = new Panel { Dock = DockStyle.Bottom, Height = 142, Padding = new Padding(10, 6, 10, 0) };
        details.Controls.Add(buttons);
        details.Controls.Add(_updateStatus);
        details.Controls.Add(_problem);
        details.Controls.Add(_desc);
        details.Controls.Add(_title);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 2, 8, 6) };
        bottom.Controls.AddRange(new Control[] { _shellLogin, _rescan, _checkUpdates, _exit, _dir });
        _shellLogin.Margin = new Padding(3, 7, 12, 3);
        _dir.Margin = new Padding(12, 9, 3, 3);

        Controls.Add(_list);
        Controls.Add(details);
        Controls.Add(bottom);

        _main.Click += (_, _) => { if (Selected() is { } e) _app.Toggle(e); };
        _stop.Click += (_, _) => { if (Selected() is { } e) _app.Stop(e); };
        _settings.Click += (_, _) => { if (Selected() is { } e) _app.OpenSettings(e); };
        _folder.Click += (_, _) => { if (Selected() is { } e) _app.OpenSettingsFolder(e); };
        _log.Click += (_, _) => { if (Selected() is { } e) _app.OpenLog(e); };
        _update.Click += (_, _) => { if (Selected() is { } e) _app.Updates.Update(e); };
        _checkUpdates.Click += (_, _) => _app.Updates.Check(manual: true);
        _actions.Click += (_, _) => ShowActions();
        _autostart.CheckedChanged += (_, _) =>
        {
            if (!_updating && Selected() is { } e) _app.SetAutostart(e, _autostart.Checked);
        };
        _shellLogin.CheckedChanged += (_, _) => { if (!_updating) _app.SetShellLogin(_shellLogin.Checked); };
        _rescan.Click += (_, _) => _app.Reload();
        _exit.Click += (_, _) => _app.ExitApp();

        _dir.Text = "Modules: " + _app.Controller.ModulesDir;
        RebuildList();
        RefreshShellLogin();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_app.Exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private ModuleEntry? Selected() =>
        _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as ModuleEntry : null;

    public void RebuildList()
    {
        var selectedId = Selected()?.Id;
        _list.BeginUpdate();
        _list.Items.Clear();
        var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        foreach (var e in _app.Controller.Modules)
        {
            var img = ModuleIcons.Get(e);
            if (img != null) images.Images.Add(e.Folder, img);
            var item = new ListViewItem(e.DisplayName) { Tag = e, Name = e.Id, ImageKey = img != null ? e.Folder : "" };
            item.SubItems.Add(e.Manifest?.Version ?? "");
            item.SubItems.Add(e.Manifest?.RunMode ?? "");
            item.SubItems.Add(_app.Controller.StateText(e));
            item.SubItems.Add(_app.Controller.AutostartText(e));
            var problem = _app.Problem(e);
            item.SubItems.Add(problem ?? "OK");
            if (problem != null) item.ForeColor = Color.Firebrick;
            _list.Items.Add(item);
        }
        _list.SmallImageList?.Dispose();
        _list.SmallImageList = images;
        _list.EndUpdate();
        var again = selectedId != null ? _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Name == selectedId) : null;
        if (again == null && _list.Items.Count > 0) again = _list.Items[0];
        if (again != null) again.Selected = true;
        UpdateDetails();
    }

    /// <summary>Called every 2 s: update state cells only (cheap).</summary>
    public void RefreshStates()
    {
        if (!Visible) return;
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not ModuleEntry e) continue;
            var text = _app.Controller.StateText(e);
            if (item.SubItems[3].Text != text) item.SubItems[3].Text = text;
        }
        UpdateDetails();
    }

    public void RefreshShellLogin()
    {
        _updating = true;
        try { _shellLogin.Checked = LoginEntry.IsShellOn(); }
        catch (Exception) { _shellLogin.Enabled = false; }
        finally { _updating = false; }
    }

    private void UpdateDetails()
    {
        var e = Selected();
        _updating = true;
        try
        {
            if (e == null)
            {
                _title.Text = _app.Controller.Modules.Count == 0 ? $"No modules found in {_app.Controller.ModulesDir}" : "";
                _desc.Text = _problem.Text = _updateStatus.Text = "";
                foreach (var b in new Control[] { _main, _stop, _actions, _settings, _folder, _log, _update, _autostart }) b.Enabled = false;
                return;
            }
            var c = _app.Controller;
            if (e.IsBroken)
            {
                _title.Text = e.FolderName;
                _desc.Text = "";
                _problem.Text = $"Broken module.json: {e.Error}";
                _updateStatus.Text = "";
                foreach (var b in new Control[] { _main, _stop, _actions, _settings, _folder, _log, _update, _autostart }) b.Enabled = false;
                return;
            }
            var m = e.Manifest!;
            var running = c.IsRunning(e);
            _title.Text = $"{m.Name} {m.Version}  ({m.RunMode}, {m.Runtime.Type}, {m.Source}): {c.StateText(e)}";
            _desc.Text = m.Description;
            _problem.Text = _app.Problem(e) ?? Runtimes.OptionalHelperNote(e) ?? "";
            _problem.ForeColor = _app.Problem(e) != null ? Color.Firebrick : SystemColors.GrayText;
            _main.Text = RunModes.MainControlLabel(m.RunMode, running);
            _main.Enabled = true;
            _stop.Visible = RunModes.StopOffered(m.RunMode);
            _stop.Enabled = running || c.OwnsLiveProcess(m.Id);
            _actions.Enabled = true;
            _settings.Enabled = ModuleController.HasSettings(e);
            _folder.Enabled = _log.Enabled = true;
            _update.Enabled = _app.Updates.CanUpdate(e);
            _updateStatus.Text = _app.Updates.Status(e);
            _checkUpdates.Enabled = !_app.Updates.Busy;
            _autostart.Visible = RunModes.AutostartAllowed(m.RunMode);
            _autostart.Enabled = c.AutostartEditable(e);
            _autostart.Checked = c.AutostartEditable(e) ? c.IsAutostartOn(e) : c.AutostartText(e).StartsWith("on");
            _autostart.Text = c.AutostartEditable(e) ? "Start at login" : $"Start at login ({c.AutostartText(e)})";
        }
        finally { _updating = false; }
    }

    private void ShowActions()
    {
        if (Selected() is not { } e) return;
        var menu = new ContextMenuStrip();
        ModuleMenu.Fill(menu.Items, e, _app);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_actions, new Point(0, _actions.Height));
    }
}
