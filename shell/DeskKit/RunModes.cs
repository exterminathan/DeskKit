// Run-mode rules (contract 7.1): pure functions, so the self-test checks them without processes.
namespace DeskKit.Shell;

public enum ToggleKind { Start, Stop, StartToggleAction }

public static class RunModes
{
    public static bool IsLongRunning(string mode) => mode is "tray" or "window";

    /// <summary>Text for the main control button, per mode and state.</summary>
    public static string MainControlLabel(string mode, bool running) => mode switch
    {
        "tray" => running ? "Turn off" : "Turn on",
        "window" => running ? "Close" : "Open",
        "popup" => "Open",
        "oneshot" => "Run",
        _ => "Start",
    };

    /// <summary>What "toggle" does (contract 7.1).</summary>
    public static ToggleKind Toggle(string mode, bool running, bool hasToggleAction) => mode switch
    {
        "tray" or "window" => running ? ToggleKind.Stop : ToggleKind.Start,
        "oneshot" => hasToggleAction ? ToggleKind.StartToggleAction : ToggleKind.Start,
        _ => ToggleKind.Start,
    };

    /// <summary>Only tray and window modules may autostart.</summary>
    public static bool AutostartAllowed(string mode) => IsLongRunning(mode);

    /// <summary>
    /// Running state (contract 7.1, 7.4): tray/window with a mutex = the mutex exists; otherwise only a process
    /// the shell started counts.
    /// </summary>
    public static bool IsRunning(string mode, string? mutex, Func<string, bool> mutexExists, bool ownProcessAlive) =>
        IsLongRunning(mode) && mutex != null ? mutexExists(mutex) : ownProcessAlive;

    /// <summary>Stop applies to tray and window modules only; popup exits by itself, oneshot is killed after 60 s.</summary>
    public static bool StopOffered(string mode) => IsLongRunning(mode);

    /// <summary>
    /// Whether the shell can try to stop: its own stop event (works for any copy, contract 7.3 step 1) or a
    /// process the shell started (steps 2 and 3). Otherwise the shell only shows a message.
    /// </summary>
    public static bool CanStop(string mode, string? stopEvent, bool ownProcessAlive) =>
        StopOffered(mode) && (stopEvent != null || ownProcessAlive);

    public static string NotStartedByShellMessage(string name) =>
        $"{name} was not started by DeskKit. Close it from its own tray icon.";

    public static string OneshotTimeoutMessage(string name) => $"{name} did not finish in 60 seconds";
}
