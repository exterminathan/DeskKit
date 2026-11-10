// Power Rename scaffold (DeskKit module "powerrename"). No features yet: it only proves the module
// builds, starts, shows "Power Rename scaffold, no features yet", and stops on its stop event.
using System.Windows.Forms;

namespace DeskKit.Modules.PowerRename;

internal static class ExitCode
{
    public const int Ok = 0, Error = 1, Usage = 2;
}

internal static class Program
{
    private const string ModuleId = "powerrename";
    private const string Name = "Power Rename";
    private const string Message = "Power Rename scaffold, no features yet";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
            return Fail(ExitCode.Usage, "Usage: PowerRename.exe (no arguments yet)");

        Log(Message);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The hidden control gives the stop thread a UI thread to marshal to.
        using var sync = new Control();
        sync.CreateControl();
        _ = sync.Handle;
        using var form = new Form
        {
            Text = Name,
            ClientSize = new System.Drawing.Size(420, 140),
            StartPosition = FormStartPosition.CenterScreen,
        };
        form.Controls.Add(new Label { Text = Message, Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter });

        using var instance = SingleInstance.TryStart(ModuleId,
            onShow: () => sync.BeginInvoke(() =>
            {
                if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                form.Activate();
            }),
            onStop: () => { Log("stop event"); sync.BeginInvoke(() => form.Close()); });
        if (instance is null) return ExitCode.Ok;   // another copy runs and was told to show itself

        Application.Run(form);
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
