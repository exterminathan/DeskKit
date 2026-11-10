using TaskbarGroups.Models;
using TaskbarGroups.Services;

namespace TaskbarGroups.Config;

public sealed class SettingsForm : Form
{
    private readonly TextBox _txtShortcutsPath;
    private readonly Label _lblEffective;
    private readonly Button _btnBrowse;
    private readonly Button _btnReset;
    private readonly CheckBox _chkStayOpen;
    private readonly Button _btnSave;
    private readonly Button _btnCancel;

    public SettingsForm()
    {
        Text = "Settings";
        Icon = Program.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 256);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9f);

        Controls.Add(new Label
        {
            Text = "Shortcuts folder",
            Location = new Point(12, 14),
            AutoSize = true,
        });

        Controls.Add(new Label
        {
            Text = $"Leave blank to use the default in the data folder ({ConfigService.DefaultShortcutsRoot}).",
            Location = new Point(12, 34),
            Size = new Size(476, 32),
            ForeColor = Color.DimGray,
        });

        _txtShortcutsPath = new TextBox
        {
            Location = new Point(12, 72),
            Size = new Size(380, 24),
            Text = SettingsService.Current.ShortcutsPath ?? "",
        };
        _txtShortcutsPath.TextChanged += (_, _) => UpdateEffectiveLabel();
        Controls.Add(_txtShortcutsPath);

        _btnBrowse = new Button
        {
            Text = "Browse...",
            Location = new Point(400, 70),
            Size = new Size(88, 28),
        };
        _btnBrowse.Click += (_, _) => Browse();
        Controls.Add(_btnBrowse);

        _btnReset = new Button
        {
            Text = "Use Default",
            Location = new Point(12, 104),
            Size = new Size(100, 28),
        };
        _btnReset.Click += (_, _) => _txtShortcutsPath.Text = "";
        Controls.Add(_btnReset);

        _lblEffective = new Label
        {
            Location = new Point(12, 142),
            Size = new Size(476, 32),
            ForeColor = Color.DimGray,
        };
        Controls.Add(_lblEffective);
        UpdateEffectiveLabel();

        _chkStayOpen = new CheckBox
        {
            Text = "Keep popup open until clicked away",
            Location = new Point(12, 178),
            AutoSize = true,
            Checked = SettingsService.Current.StayOpenUntilClickOff,
        };
        Controls.Add(_chkStayOpen);

        _btnSave = new Button
        {
            Text = "Save",
            Location = new Point(308, 216),
            Size = new Size(86, 30),
        };
        _btnSave.Click += (_, _) => Save();
        Controls.Add(_btnSave);

        _btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(402, 216),
            Size = new Size(86, 30),
            DialogResult = DialogResult.Cancel,
        };
        Controls.Add(_btnCancel);

        AcceptButton = _btnSave;
        CancelButton = _btnCancel;
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select a folder for taskbar group .lnk files",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = ResolvePath(_txtShortcutsPath.Text) ?? ConfigService.DataRoot,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _txtShortcutsPath.Text = dlg.SelectedPath;
        }
    }

    private void UpdateEffectiveLabel()
    {
        var custom = _txtShortcutsPath.Text.Trim();
        var effective = string.IsNullOrEmpty(custom) ? ConfigService.DefaultShortcutsRoot : custom;
        _lblEffective.Text = $"Effective path: {effective}";
    }

    private void Save()
    {
        var raw = _txtShortcutsPath.Text.Trim();
        string? toSave = string.IsNullOrEmpty(raw) ? null : raw;

        if (toSave is not null)
        {
            try
            {
                Directory.CreateDirectory(toSave);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not use that folder:\n{ex.Message}",
                    "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        try
        {
            SettingsService.Save(new AppSettings
            {
                ShortcutsPath = toSave,
                StayOpenUntilClickOff = _chkStayOpen.Checked,
            });
            ConfigService.EnsureDirectories();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save settings: {ex.Message}",
                "Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string? ResolvePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return Path.GetFullPath(raw); }
        catch { return null; }
    }
}
