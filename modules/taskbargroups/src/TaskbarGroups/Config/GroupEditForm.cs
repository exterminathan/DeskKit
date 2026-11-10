using TaskbarGroups.Models;
using TaskbarGroups.Services;

namespace TaskbarGroups.Config;

public sealed class GroupEditForm : Form
{
    private const string DarkHex = "#1E1E1E";
    private const string LightHex = "#F0F0F0";

    private readonly bool _isEditing;
    private readonly string _originalName;
    private readonly GroupConfig _group;

    private readonly TextBox _txtName;
    private readonly PictureBox _picIcon;
    private readonly Button _btnBrowseIcon;
    private readonly RadioButton _rbDark;
    private readonly RadioButton _rbLight;
    private readonly RadioButton _rbCustom;
    private readonly Button _btnCustomColor;
    private readonly TrackBar _trkOpacity;
    private readonly Label _lblOpacityValue;
    private readonly TrackBar _trkIconSize;
    private readonly Label _lblIconSizeValue;
    private readonly ListView _lvShortcuts;
    private readonly ImageList _shortcutImages;
    private readonly Button _btnAddShortcut;
    private readonly Button _btnRemoveShortcut;
    private readonly Button _btnSave;
    private readonly Button _btnCancel;

    private string _customColorHex = DarkHex;

    public GroupEditForm(GroupConfig? existing)
    {
        _isEditing = existing is not null;
        _group = existing is null
            ? new GroupConfig { Name = "", IconPath = "icons/link.ico", BackgroundColor = DarkHex, Opacity = 0.95, IconSize = 32 }
            : Clone(existing);
        _originalName = _isEditing ? _group.Name : "";

        Text = _isEditing ? $"Edit Group — {_group.Name}" : "New Group";
        Icon = Program.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(540, 600);
        MinimumSize = new Size(540, 600);
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        // Name
        Controls.Add(new Label { Text = "Name", Location = new Point(12, 14), AutoSize = true });
        _txtName = new TextBox
        {
            Location = new Point(80, 11),
            Size = new Size(330, 24),
            MaxLength = 49,
            Text = _group.Name,
        };
        Controls.Add(_txtName);

        // Icon
        Controls.Add(new Label { Text = "Icon", Location = new Point(12, 48), AutoSize = true });
        _picIcon = new PictureBox
        {
            Location = new Point(80, 44),
            Size = new Size(64, 64),
            BorderStyle = BorderStyle.FixedSingle,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
        };
        Controls.Add(_picIcon);
        _btnBrowseIcon = new Button
        {
            Text = "Browse…",
            Location = new Point(150, 76),
            Size = new Size(90, 28),
        };
        _btnBrowseIcon.Click += (_, _) => BrowseIcon();
        Controls.Add(_btnBrowseIcon);
        LoadIconPreview();

        // Background color
        Controls.Add(new Label { Text = "Background", Location = new Point(12, 124), AutoSize = true });
        _rbDark = new RadioButton { Text = "Dark", Location = new Point(80, 122), AutoSize = true };
        _rbLight = new RadioButton { Text = "Light", Location = new Point(150, 122), AutoSize = true };
        _rbCustom = new RadioButton { Text = "Custom", Location = new Point(220, 122), AutoSize = true };
        _btnCustomColor = new Button
        {
            Text = "Pick…",
            Location = new Point(298, 119),
            Size = new Size(60, 26),
            Enabled = false,
        };
        _btnCustomColor.Click += (_, _) => PickCustomColor();
        _rbCustom.CheckedChanged += (_, _) => _btnCustomColor.Enabled = _rbCustom.Checked;
        Controls.AddRange(new Control[] { _rbDark, _rbLight, _rbCustom, _btnCustomColor });
        ApplyColorRadios();

        // Opacity
        Controls.Add(new Label { Text = "Opacity", Location = new Point(12, 158), AutoSize = true });
        _trkOpacity = new TrackBar
        {
            Location = new Point(80, 152),
            Size = new Size(330, 45),
            Minimum = 30,
            Maximum = 100,
            TickFrequency = 10,
            Value = (int)Math.Round(Math.Clamp(_group.Opacity, 0.3, 1.0) * 100),
        };
        _lblOpacityValue = new Label
        {
            Location = new Point(416, 158),
            Size = new Size(60, 20),
            Text = $"{_trkOpacity.Value}%",
        };
        _trkOpacity.ValueChanged += (_, _) => _lblOpacityValue.Text = $"{_trkOpacity.Value}%";
        Controls.Add(_trkOpacity);
        Controls.Add(_lblOpacityValue);

        // Icon size
        Controls.Add(new Label { Text = "Icon Size", Location = new Point(12, 206), AutoSize = true });
        _trkIconSize = new TrackBar
        {
            Location = new Point(80, 200),
            Size = new Size(330, 45),
            Minimum = 16,
            Maximum = 96,
            TickFrequency = 8,
            SmallChange = 8,
            LargeChange = 16,
            Value = Math.Clamp(_group.IconSize <= 0 ? 32 : _group.IconSize, 16, 96),
        };
        _lblIconSizeValue = new Label
        {
            Location = new Point(416, 206),
            Size = new Size(60, 20),
            Text = $"{_trkIconSize.Value}px",
        };
        _trkIconSize.ValueChanged += (_, _) => _lblIconSizeValue.Text = $"{_trkIconSize.Value}px";
        Controls.Add(_trkIconSize);
        Controls.Add(_lblIconSizeValue);

        // Shortcuts list
        Controls.Add(new Label { Text = "Shortcuts", Location = new Point(12, 250), AutoSize = true });
        _shortcutImages = new ImageList { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
        _lvShortcuts = new ListView
        {
            Location = new Point(12, 272),
            Size = new Size(516, 230),
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            SmallImageList = _shortcutImages,
        };
        _lvShortcuts.Columns.Add("Name", 160);
        _lvShortcuts.Columns.Add("Target", 340);
        Controls.Add(_lvShortcuts);

        _btnAddShortcut = new Button
        {
            Text = "Add Shortcut…",
            Location = new Point(12, 510),
            Size = new Size(120, 28),
        };
        _btnAddShortcut.Click += (_, _) => AddShortcuts();
        _btnRemoveShortcut = new Button
        {
            Text = "Remove",
            Location = new Point(140, 510),
            Size = new Size(80, 28),
        };
        _btnRemoveShortcut.Click += (_, _) => RemoveSelectedShortcuts();
        var btnUp = new Button { Text = "Move Up", Location = new Point(348, 510), Size = new Size(86, 28) };
        btnUp.Click += (_, _) => MoveSelectedShortcuts(-1);
        var btnDown = new Button { Text = "Move Down", Location = new Point(442, 510), Size = new Size(86, 28) };
        btnDown.Click += (_, _) => MoveSelectedShortcuts(1);
        Controls.Add(_btnAddShortcut);
        Controls.Add(_btnRemoveShortcut);
        Controls.Add(btnUp);
        Controls.Add(btnDown);

        RefreshShortcutList();

        // Save / Cancel
        _btnSave = new Button
        {
            Text = "Save",
            Location = new Point(348, 554),
            Size = new Size(86, 30),
            DialogResult = DialogResult.None,
        };
        _btnSave.Click += (_, _) => Save();
        _btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(442, 554),
            Size = new Size(86, 30),
            DialogResult = DialogResult.Cancel,
        };
        Controls.Add(_btnSave);
        Controls.Add(_btnCancel);
        AcceptButton = _btnSave;
        CancelButton = _btnCancel;
    }

    private static GroupConfig Clone(GroupConfig src) => new()
    {
        Name = src.Name,
        IconPath = src.IconPath,
        BackgroundColor = src.BackgroundColor,
        Opacity = src.Opacity,
        IconSize = src.IconSize <= 0 ? 32 : src.IconSize,
        Shortcuts = src.Shortcuts.Select(s => new ShortcutItem
        {
            Name = s.Name,
            TargetPath = s.TargetPath,
            WorkingDirectory = s.WorkingDirectory,
            Arguments = s.Arguments,
            IconCachePath = s.IconCachePath,
        }).ToList(),
    };

    private void ApplyColorRadios()
    {
        var hex = string.IsNullOrEmpty(_group.BackgroundColor) ? DarkHex : _group.BackgroundColor;
        if (string.Equals(hex, DarkHex, StringComparison.OrdinalIgnoreCase))
        {
            _rbDark.Checked = true;
        }
        else if (string.Equals(hex, LightHex, StringComparison.OrdinalIgnoreCase))
        {
            _rbLight.Checked = true;
        }
        else
        {
            _rbCustom.Checked = true;
            _customColorHex = hex;
        }
    }

    private void PickCustomColor()
    {
        using var dlg = new ColorDialog
        {
            FullOpen = true,
            Color = TryParse(_customColorHex, Color.FromArgb(0x1E, 0x1E, 0x1E)),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _customColorHex = ColorTranslator.ToHtml(dlg.Color);
        }
    }

    private void BrowseIcon()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select group icon",
            InitialDirectory = Directory.Exists(ConfigService.IconsRoot) ? ConfigService.IconsRoot : "",
            Filter = "Icon/Image (*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.lnk)|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.lnk",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            // Stored relative when the file is inside the data dir, else absolute.
            _group.IconPath = ConfigService.ToStoredIconPath(dlg.FileName);
            LoadIconPreview();
        }
    }

    private void LoadIconPreview()
    {
        _picIcon.Image?.Dispose();
        _picIcon.Image = null;

        var iconPath = ConfigService.ResolveIconPath(_group.IconPath);
        if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
        {
            // Fall back to the icon cached when the group was saved.
            iconPath = Path.Combine(ConfigService.ConfigRoot, _group.Name, "GroupIcon.ico");
            if (string.IsNullOrEmpty(_group.Name) || !File.Exists(iconPath))
                return;
        }

        try
        {
            var ext = Path.GetExtension(iconPath).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
            {
                _picIcon.Image = Image.FromFile(iconPath);
                return;
            }
            using var icon = IconService.ExtractIcon(iconPath);
            if (icon is not null)
            {
                _picIcon.Image = icon.ToBitmap();
            }
        }
        catch { /* preview failed — leave empty */ }
    }

    private void AddShortcuts()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add shortcuts",
            Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        foreach (var path in dlg.FileNames)
        {
            _group.Shortcuts.Add(new ShortcutItem
            {
                Name = Path.GetFileNameWithoutExtension(path),
                TargetPath = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                Arguments = "",
                IconCachePath = "",
            });
        }
        RefreshShortcutList();
    }

    private void RemoveSelectedShortcuts()
    {
        var indices = _lvShortcuts.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
        foreach (var i in indices)
        {
            if (i >= 0 && i < _group.Shortcuts.Count)
                _group.Shortcuts.RemoveAt(i);
        }
        RefreshShortcutList();
    }

    private void MoveSelectedShortcuts(int delta)
    {
        var selected = _lvShortcuts.SelectedIndices.Cast<int>().OrderBy(i => i).ToList();
        if (selected.Count == 0) return;

        var list = _group.Shortcuts;
        var newSelection = new List<int>();
        if (delta < 0)
        {
            int floor = 0;
            foreach (var i in selected)
            {
                if (i == floor) { floor++; newSelection.Add(i); continue; }
                (list[i - 1], list[i]) = (list[i], list[i - 1]);
                newSelection.Add(i - 1);
            }
        }
        else
        {
            int ceil = list.Count - 1;
            foreach (var i in Enumerable.Reverse(selected))
            {
                if (i == ceil) { ceil--; newSelection.Add(i); continue; }
                (list[i + 1], list[i]) = (list[i], list[i + 1]);
                newSelection.Add(i + 1);
            }
        }

        RefreshShortcutList();
        foreach (var i in newSelection) _lvShortcuts.Items[i].Selected = true;
        _lvShortcuts.Focus();
    }

    private void RefreshShortcutList()
    {
        _lvShortcuts.BeginUpdate();
        try
        {
            _lvShortcuts.Items.Clear();
            _shortcutImages.Images.Clear();

            for (int i = 0; i < _group.Shortcuts.Count; i++)
            {
                var s = _group.Shortcuts[i];
                int imgIndex = -1;
                var icon = IconService.ExtractIcon(s.TargetPath);
                if (icon is not null)
                {
                    try
                    {
                        var bmp = icon.ToBitmap();
                        var key = $"item_{i}";
                        _shortcutImages.Images.Add(key, bmp);
                        imgIndex = _shortcutImages.Images.IndexOfKey(key);
                    }
                    catch { /* leave imgIndex = -1 */ }
                    finally
                    {
                        icon.Dispose();
                    }
                }

                var item = new ListViewItem(s.Name);
                if (imgIndex >= 0) item.ImageIndex = imgIndex;
                item.SubItems.Add(s.TargetPath);
                _lvShortcuts.Items.Add(item);
            }
        }
        finally
        {
            _lvShortcuts.EndUpdate();
        }
    }

    private void Save()
    {
        var name = _txtName.Text.Trim();
        if (!ConfigService.IsValidGroupName(name, out var err))
        {
            MessageBox.Show(err ?? "Invalid name.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_isEditing && !string.Equals(name, _originalName, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Renaming groups is not supported in this version. Delete and recreate to rename.",
                "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_isEditing && Directory.Exists(Path.Combine(ConfigService.ConfigRoot, name)))
        {
            MessageBox.Show($"A group named '{name}' already exists.",
                "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_group.Shortcuts.Count == 0)
        {
            MessageBox.Show("Add at least one shortcut before saving.",
                "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _group.Name = name;
        _group.BackgroundColor = _rbDark.Checked ? DarkHex
            : _rbLight.Checked ? LightHex
            : _customColorHex;
        _group.Opacity = _trkOpacity.Value / 100.0;
        _group.IconSize = _trkIconSize.Value;

        try
        {
            ConfigService.SaveGroup(_group);
            IconService.CacheGroupIcon(_group);
            IconService.CacheIcons(_group);
            ConfigService.SaveGroup(_group);   // re-save with updated IconCachePath values
            ShortcutService.GenerateShortcut(_group);
            if (!_isEditing)
            {
                ShortcutService.RevealShortcutsFolder();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static Color TryParse(string hex, Color fallback)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return fallback; }
    }
}
