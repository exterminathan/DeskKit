// Win32 calls the shell needs: named mutexes and events (contract 7.2, 7.3), process start with
// stdout and stderr sent straight to a log file (9.3), process trees and top-level windows (7.3).
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskKit.Shell;

internal static class Native
{
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint EVENT_MODIFY_STATE = 0x0002;
    public const uint WM_CLOSE = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenMutexW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>True when a named mutex exists: OpenMutexW(SYNCHRONIZE) succeeds (contract 7.4).</summary>
    public static bool MutexExists(string name)
    {
        var h = OpenMutexW(SYNCHRONIZE, false, name);
        if (h == IntPtr.Zero) return false;
        CloseHandle(h);
        return true;
    }

    /// <summary>OpenEventW(EVENT_MODIFY_STATE) + SetEvent. False when the event does not exist.</summary>
    public static bool SignalEvent(string name)
    {
        var h = OpenEventW(EVENT_MODIFY_STATE, false, name);
        if (h == IntPtr.Zero) return false;
        try { return SetEvent(h); }
        finally { CloseHandle(h); }
    }

    // ---- Console attach (for --list, --version, --selftest from a terminal) ----

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);

    /// <summary>A WinExe has no console. Attach to the parent's console unless stdout is already redirected.</summary>
    public static void AttachParentConsoleIfNeeded()
    {
        var h = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        if (h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) != 0) return; // redirected to a file or pipe
        if (AttachConsole(-1))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    // ---- Log file handle: FILE_APPEND_DATA, inheritable, shared ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa,
        uint disposition, uint flags, IntPtr template);

    /// <summary>Open a file for appending with an inheritable handle. Every write lands at the end.</summary>
    public static SafeFileHandle OpenAppendInheritable(string path)
    {
        const uint FILE_APPEND_DATA = 0x0004, SHARE_ALL = 0x7, OPEN_ALWAYS = 4, FILE_ATTRIBUTE_NORMAL = 0x80;
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        var h = CreateFileW(path, FILE_APPEND_DATA | SYNCHRONIZE, SHARE_ALL, ref sa, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"Cannot open log file {path} (Win32 error {Marshal.GetLastWin32Error()}).");
        return h;
    }

    // ---- CreateProcess with an explicit inherited-handle list ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, char[] cmdLine, IntPtr procAttr, IntPtr threadAttr,
        bool inherit, uint flags, string? env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value,
        IntPtr size, IntPtr prev, IntPtr retSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    /// <summary>
    /// Start a process. When <paramref name="output"/> is set, stdout and stderr go to it and it is the only
    /// handle the child inherits. Returns the process id; the caller gets a Process object before the handle closes.
    /// </summary>
    public static System.Diagnostics.Process StartProcess(string commandLine, string workingDir, string envBlock,
        bool newConsole, SafeFileHandle? output)
    {
        const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NEW_CONSOLE = 0x10, CREATE_NO_WINDOW = 0x08000000,
            EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        const int STARTF_USESTDHANDLES = 0x100;
        var PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x20002;

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        uint flags = CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT | (newConsole ? CREATE_NEW_CONSOLE : CREATE_NO_WINDOW);
        IntPtr attrList = IntPtr.Zero, handleArray = IntPtr.Zero;
        bool inherit = false;
        bool addRef = false;
        try
        {
            if (output != null)
            {
                output.DangerousAddRef(ref addRef);
                var h = output.DangerousGetHandle();
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                si.StartupInfo.hStdOutput = h;
                si.StartupInfo.hStdError = h;
                si.StartupInfo.hStdInput = IntPtr.Zero;

                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attrList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                    throw new System.ComponentModel.Win32Exception();
                handleArray = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(handleArray, h);
                if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleArray,
                        (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception();
                si.lpAttributeList = attrList;
                inherit = true;
            }

            var cmd = (commandLine + "\0").ToCharArray();
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, inherit, flags, envBlock, workingDir, ref si, out var pi))
                throw new System.ComponentModel.Win32Exception();
            try
            {
                // We still hold pi.hProcess, so the id cannot be reused before GetProcessById opens its own handle.
                return System.Diagnostics.Process.GetProcessById(pi.dwProcessId);
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (addRef) output!.DangerousRelease();
        }
    }

    // ---- Command-line parsing (self-test round trip) ----

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    public static string[] SplitCommandLine(string cmdLine)
    {
        var p = CommandLineToArgvW(cmdLine, out var n);
        if (p == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            var result = new string[n];
            for (int i = 0; i < n; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(p); }
    }

    // ---- Process tree and windows (stop protocol step 2) ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public int dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID;
        public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

    /// <summary>The process and all its descendants, by parent id.</summary>
    public static HashSet<int> ProcessTree(int rootPid)
    {
        var parentOf = new List<(int pid, int parent)>();
        var snap = CreateToolhelp32Snapshot(0x2, 0); // TH32CS_SNAPPROCESS
        if (snap != new IntPtr(-1))
        {
            try
            {
                var e = new PROCESSENTRY32W { dwSize = Marshal.SizeOf<PROCESSENTRY32W>() };
                if (Process32FirstW(snap, ref e))
                    do parentOf.Add((e.th32ProcessID, e.th32ParentProcessID)); while (Process32NextW(snap, ref e));
            }
            finally { CloseHandle(snap); }
        }
        var tree = new HashSet<int> { rootPid };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (pid, parent) in parentOf)
                if (tree.Contains(parent) && pid != parent && tree.Add(pid)) grew = true;
        }
        return tree;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    /// <summary>Post WM_CLOSE to every top-level window owned by one of these processes. Returns the count.</summary>
    public static int PostCloseToWindows(HashSet<int> pids)
    {
        int count = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid) && PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }
}
