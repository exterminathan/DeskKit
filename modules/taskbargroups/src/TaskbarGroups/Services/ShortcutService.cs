using System.Diagnostics;
using TaskbarGroups.Models;

namespace TaskbarGroups.Services;

public static class ShortcutService
{
    public static string GenerateShortcut(GroupConfig group)
    {
        Directory.CreateDirectory(ConfigService.ShortcutsRoot);
        var lnkPath = Path.Combine(ConfigService.ShortcutsRoot, group.Name + ".lnk");
        var exePath = GetExecutablePath();
        var iconPath = Path.Combine(ConfigService.ConfigRoot, group.Name, "GroupIcon.ico");

        ShellLink.Create(
            lnkPath: lnkPath,
            targetPath: exePath,
            arguments: $"\"{group.Name}\"",
            workingDirectory: ConfigService.ExeDir,
            iconPath: iconPath,
            description: $"Taskbar Group: {group.Name}");

        return lnkPath;
    }

    public static void RevealShortcutsFolder()
    {
        Directory.CreateDirectory(ConfigService.ShortcutsRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{ConfigService.ShortcutsRoot}\"",
            UseShellExecute = true,
        });
    }

    private static string GetExecutablePath()
    {
        // When run as a single-file exe, Environment.ProcessPath is the .exe.
        // In dev (dotnet run), it points at dotnet.exe — fall back to AppContext.BaseDirectory + assembly name.
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "TaskbarGroups", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }
        return Path.Combine(ConfigService.ExeDir, "TaskbarGroups.exe");
    }
}
