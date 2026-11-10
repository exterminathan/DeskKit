using TaskbarGroups.Models;
using TaskbarGroups.Services;

namespace TaskbarGroups.Config;

public sealed class ConfigForm : Form
{
    private readonly ListView _list;
    private readonly ImageList _imageList;
    private readonly Button _btnNew;
    private readonly Button _btnEdit;
    private readonly Button _btnDelete;
    private readonly Button _btnSettings;
    private readonly Button _btnOpenShortcuts;

    public ConfigForm()
    {
        Text = "Taskbar Groups";
        Icon = Program.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 520);
        MinimumSize = new Size(400, 400);
        Font = new Font("Segoe UI", 9f);

        _imageList = new ImageList { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };

        _list = new ListView
        {
            Location = new Point(12, 12),
            Size = new Size(ClientSize.Width - 24, 380),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            SmallImageList = _imageList,
            LargeImageList = _imageList,
        };
        _list.Columns.Add("Group", 240);
        _list.Columns.Add("Shortcuts", 120);
        _list.SelectedIndexChanged += (_, _) => UpdateButtonStates();
        _list.DoubleClick += (_, _) => EditSelected();

        int btnY = 408;
        _btnNew = MakeButton("New Group", 12, btnY, 80);
        _btnNew.Click += (_, _) => OpenEditor(existing: null);

        _btnEdit = MakeButton("Edit", 100, btnY, 60);
        _btnEdit.Click += (_, _) => EditSelected();

        _btnDelete = MakeButton("Delete", 168, btnY, 60);
        _btnDelete.Click += (_, _) => DeleteSelected();

        _btnSettings = MakeButton("Settings...", 236, btnY, 80);
        _btnSettings.Click += (_, _) => OpenSettings();

        _btnOpenShortcuts = MakeButton("Open Shortcuts Folder", 324, btnY, 160);
        _btnOpenShortcuts.Click += (_, _) =>
        {
            ConfigService.EnsureDirectories();
            ShortcutService.RevealShortcutsFolder();
        };

        var hint = new Label
        {
            Text = "After saving a group, right-click its .lnk in the Shortcuts folder → Pin to taskbar.",
            Location = new Point(12, 444),
            Size = new Size(ClientSize.Width - 24, 60),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = Color.DimGray,
        };

        Controls.Add(_list);
        Controls.Add(_btnNew);
        Controls.Add(_btnEdit);
        Controls.Add(_btnDelete);
        Controls.Add(_btnSettings);
        Controls.Add(_btnOpenShortcuts);
        Controls.Add(hint);

        Load += (_, _) => RefreshGroups();
    }

    private static Button MakeButton(string text, int x, int y, int width = 92) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 28),
        Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
    };

    private void OpenSettings()
    {
        using var dlg = new SettingsForm();
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            RefreshGroups();
        }
    }

    private void RefreshGroups()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _imageList.Images.Clear();

            foreach (var g in ConfigService.GetAllGroups())
            {
                var iconPath = Path.Combine(ConfigService.ConfigRoot, g.Name, "GroupIcon.ico");
                int imgIndex = -1;
                if (File.Exists(iconPath))
                {
                    try
                    {
                        Bitmap bmp;
                        using (var icon = new Icon(iconPath))
                        {
                            bmp = icon.ToBitmap();
                        }
                        _imageList.Images.Add(g.Name, bmp);
                        imgIndex = _imageList.Images.IndexOfKey(g.Name);
                    }
                    catch { /* leave imgIndex = -1 */ }
                }

                var item = new ListViewItem(g.Name) { Tag = g.Name };
                if (imgIndex >= 0) item.ImageIndex = imgIndex;
                item.SubItems.Add(g.Shortcuts.Count.ToString());
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        var hasSelection = _list.SelectedItems.Count > 0;
        _btnEdit.Enabled = hasSelection;
        _btnDelete.Enabled = hasSelection;
    }

    private void EditSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        var name = (string)_list.SelectedItems[0].Tag!;
        var existing = ConfigService.LoadGroup(name);
        if (existing is null)
        {
            MessageBox.Show($"Group '{name}' could not be loaded.");
            RefreshGroups();
            return;
        }
        OpenEditor(existing);
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        var name = (string)_list.SelectedItems[0].Tag!;
        var result = MessageBox.Show(
            $"Delete group '{name}'?\n\nThis removes the config folder and the .lnk file. The pinned taskbar icon will remain but will no longer work.",
            "Delete group",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;

        try
        {
            ConfigService.DeleteGroup(name);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Delete failed: {ex.Message}");
        }
        RefreshGroups();
    }

    private void OpenEditor(GroupConfig? existing)
    {
        using var editor = new GroupEditForm(existing);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            RefreshGroups();
        }
    }
}
