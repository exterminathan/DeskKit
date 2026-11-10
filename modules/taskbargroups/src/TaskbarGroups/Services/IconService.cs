using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TaskbarGroups.Models;

namespace TaskbarGroups.Services;

public static class IconService
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIcons(
        string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, int[] piconid, int nIcons, int flags);

    public static Icon? ExtractIcon(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
            return null;

        try
        {
            var ext = Path.GetExtension(targetPath).ToLowerInvariant();
            if (ext == ".ico")
            {
                return new Icon(targetPath);
            }
            if (ext == ".lnk")
            {
                // Honor the .lnk's own IconLocation first (e.g. system DLLs like imageres.dll,15).
                // This is what Windows displays for the shortcut and what the user expects to keep.
                var (iconPath, iconIndex) = ShellLink.GetIconLocation(targetPath);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    var expanded = Environment.ExpandEnvironmentVariables(iconPath);
                    if (File.Exists(expanded))
                    {
                        var fromLnk = ExtractIconAtIndex(expanded, iconIndex);
                        if (fromLnk is not null) return fromLnk;
                    }
                }

                // Fallback: resolve target and extract from there.
                var resolved = ShellLink.ResolveTarget(targetPath);
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                {
                    return ExtractIconAtIndex(resolved, 0) ?? Icon.ExtractAssociatedIcon(resolved);
                }
                return Icon.ExtractAssociatedIcon(targetPath);
            }
            return ExtractIconAtIndex(targetPath, 0) ?? Icon.ExtractAssociatedIcon(targetPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ExtractIcon failed for '{targetPath}': {ex.Message}");
            return null;
        }
    }

    private static Icon? ExtractIconAtIndex(string path, int index, int size = 256)
    {
        var handles = new IntPtr[1];
        var ids = new int[1];
        int n = PrivateExtractIcons(path, index, size, size, handles, ids, 1, 0);
        if (n > 0 && handles[0] != IntPtr.Zero)
        {
            try
            {
                using var raw = Icon.FromHandle(handles[0]);
                return (Icon)raw.Clone();
            }
            finally
            {
                DestroyIcon(handles[0]);
            }
        }
        return null;
    }

    public static void CacheIcons(GroupConfig group)
    {
        var iconsDir = Path.Combine(ConfigService.ConfigRoot, group.Name, "icons");
        Directory.CreateDirectory(iconsDir);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in group.Shortcuts)
        {
            var safe = ConfigService.SanitizeFileName(string.IsNullOrEmpty(s.Name)
                ? Path.GetFileNameWithoutExtension(s.TargetPath)
                : s.Name);

            var fileName = safe + ".ico";
            int suffix = 1;
            while (used.Contains(fileName))
            {
                fileName = $"{safe}_{suffix++}.ico";
            }
            used.Add(fileName);

            var fullPath = Path.Combine(iconsDir, fileName);
            using var icon = ExtractIcon(s.TargetPath) ?? SystemIcons.Application;
            try
            {
                using var fs = File.Create(fullPath);
                icon.Save(fs);
                s.IconCachePath = Path.Combine("icons", fileName).Replace('\\', '/');
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to cache icon for '{s.Name}': {ex.Message}");
                s.IconCachePath = "";
            }
        }
    }

    public static void CacheGroupIcon(GroupConfig group)
    {
        // A relative iconPath is resolved against the data dir; a stored absolute path is kept.
        var iconPath = ConfigService.ResolveIconPath(group.IconPath);
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
            return;

        var groupDir = Path.Combine(ConfigService.ConfigRoot, group.Name);
        Directory.CreateDirectory(groupDir);
        var dest = Path.Combine(groupDir, "GroupIcon.ico");

        var ext = Path.GetExtension(iconPath).ToLowerInvariant();
        try
        {
            if (ext == ".ico")
            {
                File.Copy(iconPath, dest, overwrite: true);
                return;
            }
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
            {
                using var bmp = new Bitmap(iconPath);
                SaveBitmapAsIcon(bmp, dest);
                return;
            }
            // .exe / .lnk / other — extract associated icon
            using var icon = ExtractIcon(iconPath);
            if (icon is null) return;
            using var fs = File.Create(dest);
            icon.Save(fs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CacheGroupIcon failed: {ex.Message}");
        }
    }

    public static Icon LoadCachedIcon(GroupConfig group, ShortcutItem s)
    {
        if (!string.IsNullOrEmpty(s.IconCachePath))
        {
            var full = Path.Combine(ConfigService.ConfigRoot, group.Name, s.IconCachePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                try { return new Icon(full); }
                catch { /* fall through */ }
            }
        }
        return SystemIcons.Application;
    }

    private static void SaveBitmapAsIcon(Bitmap source, string destPath)
    {
        // Resize to 256x256 max while keeping aspect, then encode as PNG inside an .ico container.
        int size = Math.Min(256, Math.Max(source.Width, source.Height));
        using var resized = new Bitmap(size, size);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.DrawImage(source, 0, 0, size, size);
        }

        using var pngStream = new MemoryStream();
        resized.Save(pngStream, ImageFormat.Png);
        var pngBytes = pngStream.ToArray();

        using var fs = File.Create(destPath);
        using var bw = new BinaryWriter(fs);
        // ICONDIR
        bw.Write((short)0);            // reserved
        bw.Write((short)1);            // type: icon
        bw.Write((short)1);            // count
        // ICONDIRENTRY
        bw.Write((byte)(size >= 256 ? 0 : size));   // width
        bw.Write((byte)(size >= 256 ? 0 : size));   // height
        bw.Write((byte)0);             // colors in palette
        bw.Write((byte)0);             // reserved
        bw.Write((short)1);            // color planes
        bw.Write((short)32);           // bits per pixel
        bw.Write(pngBytes.Length);     // bytes of image data
        bw.Write(6 + 16);              // offset to image data
        bw.Write(pngBytes);
    }
}
