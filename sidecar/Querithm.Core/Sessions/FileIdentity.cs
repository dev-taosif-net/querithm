using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Querithm.Core.Sessions;

/// <summary>
/// A file's identity as the shell records it in a grant (Phase D0c): "u:&lt;dev&gt;:&lt;ino&gt;" on Unix,
/// "w:&lt;volume serial&gt;:&lt;file index&gt;" on Windows. It survives writes to the file and changes when the path is
/// replaced by another file. .NET has no public API for it: Unix reads the runtime's own stat shim (System.Native), whose
/// layout is checked against values .NET does expose (size, permission bits) and fails closed when they disagree;
/// Windows asks GetFileInformationByHandle.
/// </summary>
public static class FileIdentity
{
    /// <summary>The identity of the file at <paramref name="path"/>; null when it does not exist or is not a regular file.</summary>
    /// <exception cref="PlatformNotSupportedException">The identity cannot be read reliably here; the caller refuses the open.</exception>
    public static string? Of(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory)) return null;
        return OperatingSystem.IsWindows() ? Windows(path) : Unix(path, info);
    }

    // ------------------------------------------------------------------ Unix: System.Native's FileStatus (pal_io.h)

    [StructLayout(LayoutKind.Sequential)]
    struct FileStatus
    {
        public int Flags;
        public int Mode;
        public uint Uid;
        public uint Gid;
        public long Size;
        public long ATime, ATimeNsec, MTime, MTimeNsec, CTime, CTimeNsec, BirthTime, BirthTimeNsec;
        public long Dev;
        public long RDev;
        public long Ino;
        public uint UserFlags;
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", SetLastError = true)]
    static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus output);

    const int S_IFMT = 0xF000, S_IFREG = 0x8000;

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    static string Unix(string path, FileInfo info)
    {
        FileStatus st;
        try
        {
            if (Stat(path, out st) != 0) return null!;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException("The file identity cannot be read on this system.", ex);
        }
        // The shim is internal to .NET: confirm the layout with what .NET reports itself, and refuse when it disagrees
        // (a regular file has no device number of its own, and a real device and inode).
        var mode = (int)File.GetUnixFileMode(path) & 0xFFF;
        if ((st.Mode & S_IFMT) != S_IFREG || (st.Mode & 0xFFF) != mode || st.Size != info.Length || st.RDev != 0 || st.Ino == 0)
            throw new PlatformNotSupportedException("The file identity cannot be read reliably on this system.");
        return $"u:{(ulong)st.Dev}:{(ulong)st.Ino}";
    }

    // ------------------------------------------------------------------ Windows

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    const uint FILE_READ_ATTRIBUTES = 0x80, FILE_SHARE_ALL = 0x7 /* read | write | delete */, OPEN_EXISTING = 3;

    static string? Windows(string path)
    {
        // Attributes only: sharing is checked for data access, so this handle is granted even while an engine holds the
        // file without sharing (DuckDB opens a writable database so). A read handle was refused there, and the check
        // after the driver's open then reported the file gone.
        using var handle = CreateFile(Extended(Path.GetFullPath(path)), FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException(Marshal.GetLastPInvokeErrorMessage(), Marshal.GetHRForLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info)) throw new PlatformNotSupportedException("The file identity cannot be read on this system.");
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return $"w:{info.VolumeSerialNumber}:{index}";
    }

    /// <summary>CreateFileW stops at 260 characters unless the path has the extended prefix (.NET's own file calls add it).</summary>
    static string Extended(string full) =>
        full.Length < 260 || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) ? full
        : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..]
        : @"\\?\" + full;
}
