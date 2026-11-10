// Always On Top scaffold (DeskKit module "alwaysontop"). No features yet: it only proves the module
// builds, starts, shows "Always On Top scaffold, no features yet", and stops on its stop event.
using System.Windows.Forms;

namespace DeskKit.Modules.AlwaysOnTop;

internal static class ExitCode
{
    public const int Ok = 0, Error = 1, Usage = 2;
}

internal static class Program
{
    private const string ModuleId = "alwaysontop";
    private const string Name = "Always On Top";
    private const string Message = "Always On Top scaffold, no features yet";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
            return Fail(ExitCode.Usage, "Usage: AlwaysOnTop.exe (no arguments yet)");

        Log(Message);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The hidden control gives the stop thread a UI thread to marshal to.
        using var sync = new Control();
        sync.CreateControl();
        _ = sync.Handle;
        using var menu = new ContextMenuStrip();
        menu.Items.Add(Message).Enabled = false;
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        using var icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = Name,
            ContextMenuStrip = menu,
            Visible = true,
        };

        using var instance = SingleInstance.TryStart(ModuleId,
            onShow: () => sync.BeginInvoke(() => icon.ShowBalloonTip(2000, Name, Message, ToolTipIcon.Info)),
            onStop: () => { Log("stop event"); sync.BeginInvoke(Application.Exit); });
        if (instance is null) return ExitCode.Ok;   // another copy runs and was told to show itself

        icon.ShowBalloonTip(2000, Name, Message, ToolTipIcon.Info);
        Application.Run();
        icon.Visible = false;
        return ExitCode.Ok;
    }

    /// <summary>%APPDATA%\DeskKit\&lt;id&gt;, or what the shell passed in DESKKIT_DATA_DIR.</summary>
    private static string DataDir() =>
        Environment.GetEnvironmentVariable("DESKKIT_DATA_DIR") is { Length: > 0 } passed
            ? passed
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskKit", ModuleId);

    private static void Log(string line)
    {
        try
        {
            var dir = Path.Combine(DataDir(), "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, ModuleId + ".log"), $"{DateTime.Now:s} {line}{Environment.NewLine}");
        }
        catch { /* logging must never stop the tool */ }
    }

    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        if (Environment.GetEnvironmentVariable("DESKKIT_SHELL") is null)
            MessageBox.Show(message, Name);
        return code;
    }
}
