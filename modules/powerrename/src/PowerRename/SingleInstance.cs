// Single-instance rule for tray and window modules (docs/module-contract.md 7.2).
// Names: mutex Local\DeskKit.<id>, events Local\DeskKit.<id>.show and Local\DeskKit.<id>.stop.
namespace DeskKit.Modules.PowerRename;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _stop;
    private readonly EventWaitHandle _quit = new(false, EventResetMode.ManualReset);

    private SingleInstance(Mutex mutex, EventWaitHandle show, EventWaitHandle stop, Action onShow, Action onStop)
    {
        _mutex = mutex;
        _show = show;
        _stop = stop;
        var watcher = new Thread(() =>
        {
            var handles = new WaitHandle[] { _show, _stop, _quit };
            while (true)
            {
                switch (WaitHandle.WaitAny(handles))
                {
                    case 0: onShow(); break;
                    case 1: onStop(); return;   // onStop must save state and exit within 5 s
                    default: return;
                }
            }
        }) { IsBackground = true, Name = "DeskKit single instance" };
        watcher.Start();
    }

    /// <summary>
    /// Returns an instance when this is the first copy. Returns null when another copy already runs;
    /// that copy has been told to show itself, and the caller must exit with code 0.
    /// onShow and onStop run on a background thread: marshal to the UI thread (Control.BeginInvoke).
    /// </summary>
    public static SingleInstance? TryStart(string moduleId, Action onShow, Action onStop)
    {
        var name = $@"Local\DeskKit.{moduleId}";
        var mutex = new Mutex(false, name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            if (EventWaitHandle.TryOpenExisting(name + ".show", out var other))
            {
                using (other) other.Set();
            }
            return null;
        }
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show");
        var stop = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".stop");
        return new SingleInstance(mutex, show, stop, onShow, onStop);
    }

    public void Dispose()
    {
        _quit.Set();
        _show.Dispose();
        _stop.Dispose();
        _mutex.Dispose();
        _quit.Dispose();
    }
}
