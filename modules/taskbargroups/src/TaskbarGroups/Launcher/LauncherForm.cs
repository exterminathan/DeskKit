using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using TaskbarGroups.Models;
using TaskbarGroups.Services;

namespace TaskbarGroups.Launcher;

public sealed class LauncherForm : Form
{
    private const int CornerRadius = 12;
    private const int GridPadding = 8;
    private const int HeaderHeight = 28;
    private const int Columns = 4;
    private const int MinIconSize = 16;
    private const int MaxIconSize = 96;

    private static readonly Color HoverOverlay = Color.FromArgb(40, 255, 255, 255);

    private readonly GroupConfig _group;
    private readonly Color _baseColor;
    private readonly int _iconSize;
    private readonly int _cellWidth;
    private readonly int _cellHeight;
    private bool _suppressDeactivate;
    private HookProc? _hookProc;
    private IntPtr _mouseHook;

    public LauncherForm(string groupName)
    {
        var loaded = ConfigService.LoadGroup(groupName);
        if (loaded is null)
        {
            MessageBox.Show($"Group '{groupName}' not found.", "Taskbar Groups",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(1);
        }

        _group = loaded!;
        _baseColor = ParseColor(_group.BackgroundColor, fallback: Color.FromArgb(30, 30, 30));
        _iconSize = Math.Clamp(_group.IconSize <= 0 ? 32 : _group.IconSize, MinIconSize, MaxIconSize);
        _cellWidth = _iconSize + 64;
        _cellHeight = _iconSize + 56;

        SuspendLayout();
        ConfigureForm();
        BuildLayout();
        ResumeLayout(performLayout: true);

        Load += OnLoaded;
    }

    private void ConfigureForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = _baseColor;
        ForeColor = Color.White;
        Opacity = Math.Clamp(_group.Opacity, 0.3, 1.0);
        Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        Text = _group.Name;

        if (SettingsService.Current.StayOpenUntilClickOff)
        {
            // Install a global low-level mouse hook so we close only when the
            // user physically clicks outside the popup, not on focus changes.
            _hookProc = MouseHookCallback;
            _mouseHook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _hookProc, IntPtr.Zero, 0);
            FormClosed += (_, _) =>
            {
                if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            };
        }
        else
        {
            Deactivate += (_, _) =>
            {
                if (_suppressDeactivate) return;
                Close();
            };
        }
    }

    private void BuildLayout()
    {
        var count = Math.Max(1, _group.Shortcuts.Count);
        var rows = (count + Columns - 1) / Columns;
        var cols = Math.Min(Columns, Math.Max(1, _group.Shortcuts.Count));

        var width = cols * _cellWidth + GridPadding * 2;
        var height = HeaderHeight + rows * _cellHeight + GridPadding * 2;
        ClientSize = new Size(width, height);

        var header = new Label
        {
            Text = _group.Name,
            Dock = DockStyle.Top,
            Height = HeaderHeight,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(GridPadding + 2, 0, 0, 0),
            ForeColor = Color.FromArgb(180, 180, 180),
            BackColor = _baseColor,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = Columns,
            RowCount = rows,
            Padding = new Padding(GridPadding),
            BackColor = _baseColor,
            AutoSize = false,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
        };
        for (int c = 0; c < Columns; c++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _cellWidth));
        for (int r = 0; r < rows; r++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, _cellHeight));

        for (int i = 0; i < _group.Shortcuts.Count; i++)
        {
            var s = _group.Shortcuts[i];
            var btn = CreateShortcutButton(s, i);
            int row = i / Columns;
            int col = i % Columns;
            grid.Controls.Add(btn, col, row);
        }

        Controls.Add(grid);
        Controls.Add(header);
    }

    private Button CreateShortcutButton(ShortcutItem s, int index)
    {
        Image? iconImage = null;
        try
        {
            using var icon = IconService.LoadCachedIcon(_group, s);
            iconImage = new Bitmap(icon.ToBitmap(), new Size(_iconSize, _iconSize));
        }
        catch { /* leave null */ }

        var label = string.IsNullOrEmpty(s.Name)
            ? Path.GetFileNameWithoutExtension(s.TargetPath)
            : s.Name;

        var btn = new Button
        {
            Text = $" {label}",
            Image = iconImage,
            ImageAlign = ContentAlignment.TopCenter,
            TextAlign = ContentAlignment.BottomCenter,
            TextImageRelation = TextImageRelation.ImageAboveText,
            FlatStyle = FlatStyle.Flat,
            BackColor = _baseColor,
            ForeColor = Color.White,
            Margin = Padding.Empty,
            Padding = new Padding(4, 6, 4, 4),
            Width = _cellWidth,
            Height = _cellHeight,
            TabStop = false,
            UseVisualStyleBackColor = false,
            Font = new Font("Segoe UI", 8.25f, FontStyle.Regular),
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = HoverOverlay;
        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(70, 255, 255, 255);

        btn.Click += (_, _) => Launch(s);
        return btn;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        ApplyRoundedCorners();
        PositionNearCursor();
        Activate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundedCorners();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        int idx = -1;
        if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9) idx = e.KeyCode - Keys.D1;
        else if (e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad9) idx = e.KeyCode - Keys.NumPad1;

        if (idx >= 0 && idx < _group.Shortcuts.Count)
        {
            e.Handled = true;
            Launch(_group.Shortcuts[idx]);
            return;
        }

        base.OnKeyDown(e);
    }

    private void Launch(ShortcutItem s)
    {
        _suppressDeactivate = true;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = s.TargetPath,
                Arguments = s.Arguments ?? "",
                WorkingDirectory = string.IsNullOrEmpty(s.WorkingDirectory)
                    ? Path.GetDirectoryName(s.TargetPath) ?? ""
                    : s.WorkingDirectory,
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch '{s.Name}':\n{ex.Message}",
                "Taskbar Groups", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (SettingsService.Current.StayOpenUntilClickOff)
            {
                _suppressDeactivate = false;
                Activate();
            }
            else
            {
                Close();
            }
        }
    }

    private void PositionNearCursor()
    {
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor);
        var work = screen.WorkingArea;

        int bottomLimit = work.Bottom;
        if (TryGetBottomTaskbarTop(screen, out int taskbarTop))
        {
            bottomLimit = Math.Min(bottomLimit, taskbarTop);
        }

        int x = cursor.X - Width / 2;
        x = Math.Clamp(x, work.Left + 8, work.Right - Width - 8);

        int y = bottomLimit - Height - 8;
        y = Math.Max(work.Top + 8, y);

        Location = new Point(x, y);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_RBUTTONDOWN = 0x0204;

        if (nCode >= 0 && ((int)wParam == WM_LBUTTONDOWN || (int)wParam == WM_RBUTTONDOWN))
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var pt = new Point(info.pt.x, info.pt.y);
            if (!Bounds.Contains(pt) && !IsDisposed)
                Close();
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool TryGetBottomTaskbarTop(Screen targetScreen, out int top)
    {
        top = 0;
        var hwnd = FindWindow("Shell_TrayWnd", null);
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;

        var bounds = targetScreen.Bounds;
        var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (!rect.IntersectsWith(bounds)) return false;

        // Only treat as bottom-anchored when the taskbar's top edge lies in the
        // lower half of this monitor (covers vertical/top taskbars without false-positive).
        if (rect.Top <= bounds.Top + bounds.Height / 2) return false;

        top = rect.Top;
        return true;
    }

    private void ApplyRoundedCorners()
    {
        // Try Windows 11 native rounded corners first.
        try
        {
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch { /* older OS — fallback below */ }

        // Region fallback (also harmless on Win11; keeps consistent shape against transparent edges).
        using var path = new GraphicsPath();
        var rect = new Rectangle(0, 0, Width, Height);
        int d = CornerRadius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex); }
        catch { return fallback; }
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public NativePoint pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
