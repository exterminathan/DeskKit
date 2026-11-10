using TaskbarGroups.Config;
using TaskbarGroups.Launcher;
using TaskbarGroups.Services;

namespace TaskbarGroups;

internal static class ExitCode
{
    public const int Ok = 0, Error = 1, Usage = 2;
}

internal static class Program
{
    internal static readonly Icon? AppIcon = LoadAppIcon();

    private static Icon? LoadAppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    }

    /// <summary>
    /// No args: the config window. One arg: the popup for that group. Run mode "popup"
    /// (docs/module-contract.md 7.1): no background process, and two copies at once are safe.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length > 1)
            return Fail(ExitCode.Usage,
                "Usage: TaskbarGroups.exe [groupName]. Run it without arguments to open the config window.");

        try
        {
            MigrationService.Run();
            ConfigService.EnsureDirectories();
        }
        catch (Exception ex)
        {
            return Fail(ExitCode.Error, $"Cannot use the data folder {ConfigService.DataRoot}: {ex.Message}");
        }

        if (args.Length == 0)
        {
            Application.Run(new ConfigForm());
            return ExitCode.Ok;
        }

        var name = args[0];
        if (ConfigService.LoadGroupSafe(name, out var loadError) is null)
            return Fail(ExitCode.Error, loadError
                ?? $"Group '{name}' not found in {ConfigService.ConfigRoot}. Open Taskbar Groups without arguments to create it.");

        Application.Run(new LauncherForm(name));
        return ExitCode.Ok;
    }

    /// <summary>One ERROR line on stderr; a message box too when no shell runs us (contract 9.2).</summary>
    internal static int Fail(int code, string message)
    {
        try { Console.Error.WriteLine($"ERROR: {message}"); } catch { /* no stderr */ }
        if (Environment.GetEnvironmentVariable("DESKKIT_SHELL") != "1")
            MessageBox.Show(message, "Taskbar Groups", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return code;
    }
}
