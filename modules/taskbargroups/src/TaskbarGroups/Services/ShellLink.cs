using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TaskbarGroups.Services;

internal static class ShellLink
{
    public static string ResolveTarget(string lnkPath)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(MAX_PATH);
            var data = new WIN32_FIND_DATAW();
            link.GetPath(sb, sb.Capacity, ref data, 0);
            return sb.ToString();
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static (string path, int index) GetIconLocation(string lnkPath)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(MAX_PATH);
            link.GetIconLocation(sb, sb.Capacity, out int index);
            return (sb.ToString(), index);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static void Create(
        string lnkPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string iconPath,
        string description)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            link.SetPath(targetPath);
            if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
            if (!string.IsNullOrEmpty(workingDirectory)) link.SetWorkingDirectory(workingDirectory);
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath)) link.SetIconLocation(iconPath, 0);
            if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private const int MAX_PATH = 260;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch,
            ref WIN32_FIND_DATAW pfd,
            uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotKey(out short pwHotkey);
        void SetHotKey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cch,
            out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }
}
