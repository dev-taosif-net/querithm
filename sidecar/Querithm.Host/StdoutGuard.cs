using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Querithm.Host;

/// <summary>
/// stdout carries JSON-RPC, and native libraries write to the process's file descriptor 1 directly, past Console.SetOut:
/// DuckDB prints its progress bar there (Phase D3, measured). JSON-RPC moves to a private duplicate of descriptor 1, and
/// descriptor 1 is pointed at stderr, so a native write lands in the log stream instead of corrupting a message. When the
/// platform calls are unavailable, the plain stdout stream is used, as before.
/// </summary>
public static class StdoutGuard
{
    public static Stream Claim()
    {
        try
        {
            return OperatingSystem.IsWindows() ? ClaimWindows() : ClaimUnix();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return Console.OpenStandardOutput();
        }
    }

    static Stream ClaimUnix()
    {
        var rpc = Unix.dup(1);
        if (rpc < 0) return Console.OpenStandardOutput();
        if (Unix.dup2(2, 1) < 0)
        {
            Unix.close(rpc);
            return Console.OpenStandardOutput();
        }
        return new FileStream(new SafeFileHandle(rpc, ownsHandle: true), FileAccess.Write, bufferSize: 0);
    }

    /// <summary>The C runtime native libraries link (ucrtbase) owns descriptor 1; its _dup2 also repoints the Win32 standard handle.</summary>
    static Stream ClaimWindows()
    {
        var rpc = Ucrt._dup(1);
        if (rpc < 0) return Console.OpenStandardOutput();
        var handle = Ucrt._get_osfhandle(rpc);
        if (handle == -1 || Ucrt._dup2(2, 1) < 0) return Console.OpenStandardOutput();
        return new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write, bufferSize: 0);
    }

    /// <summary>Test hook (--stdout-guard-test): a write to descriptor 1 as native code makes it, bypassing .NET.</summary>
    public static void NativeWrite(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (OperatingSystem.IsWindows()) Ucrt._write(1, bytes, (uint)bytes.Length);
        else Unix.write(1, bytes, (nint)bytes.Length);
    }

    static class Unix
    {
        [DllImport("libc", SetLastError = true)] public static extern int dup(int fd);
        [DllImport("libc", SetLastError = true)] public static extern int dup2(int fd, int fd2);
        [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] public static extern nint write(int fd, byte[] buffer, nint count);
    }

    static class Ucrt
    {
        [DllImport("ucrtbase.dll")] public static extern int _dup(int fd);
        [DllImport("ucrtbase.dll")] public static extern int _dup2(int fd, int fd2);
        [DllImport("ucrtbase.dll")] public static extern nint _get_osfhandle(int fd);
        [DllImport("ucrtbase.dll")] public static extern int _write(int fd, byte[] buffer, uint count);
    }
}
