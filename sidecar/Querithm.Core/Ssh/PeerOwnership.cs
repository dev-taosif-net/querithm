using System.Net;
using System.Runtime.InteropServices;

namespace Querithm.Core.Ssh;

/// <summary>
/// Which process opened a loopback TCP connection: the SSH forward accepts only connections whose
/// client socket belongs to the sidecar itself (its database drivers), so another local program cannot use the tunnel.
/// Linux reads /proc; Windows asks the TCP table for the owning PID; elsewhere the OS cannot tell and the caller
/// falls back to its connect-window gate.
/// </summary>
public static class PeerOwnership
{
    /// <summary>True/false when the OS can tell whether this process owns the client end; null when it cannot.</summary>
    public static bool? IsOwnConnection(IPEndPoint client, IPEndPoint server)
    {
        // Where the OS can tell, any failure to find the owner denies the connection (fail closed).
        try
        {
            if (OperatingSystem.IsLinux()) return Linux(client, server);
            if (OperatingSystem.IsWindows()) return Windows(client, server);
        }
        catch (Exception) { return false; }
        return null;
    }

    // ---------------------------------------------------------------------------------------------- Linux

    /// <summary>/proc/net/tcp address form: the IPv4 bytes as a little-endian 32-bit hex value, then the port in hex.</summary>
    public static string ProcAddress(IPEndPoint ep)
    {
        var b = ep.Address.MapToIPv4().GetAddressBytes();
        return $"{b[3]:X2}{b[2]:X2}{b[1]:X2}{b[0]:X2}:{ep.Port:X4}";
    }

    /// <summary>/proc/net/tcp6 form of an IPv4 endpoint seen through a dual-stack socket (::ffff:a.b.c.d), four little-endian words.</summary>
    public static string ProcAddress6(IPEndPoint ep)
    {
        var b = ep.Address.MapToIPv6().GetAddressBytes();
        var sb = new System.Text.StringBuilder(32);
        for (var w = 0; w < 4; w++)
            for (var i = 3; i >= 0; i--) sb.Append(b[w * 4 + i].ToString("X2"));
        return $"{sb}:{ep.Port:X4}";
    }

    /// <summary>The inode of the client end: an IPv4 socket in /proc/net/tcp, or a dual-stack one in /proc/net/tcp6.</summary>
    static string? FindInode(IPEndPoint client, IPEndPoint server)
    {
        foreach (var (file, local, remote) in new[] { ("/proc/net/tcp", ProcAddress(client), ProcAddress(server)), ("/proc/net/tcp6", ProcAddress6(client), ProcAddress6(server)) })
        {
            if (!File.Exists(file)) continue;
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length > 9 && f[1] == local && f[2] == remote) return f[9];
            }
        }
        return null;
    }

    static bool Linux(IPEndPoint client, IPEndPoint server)
    {
        var inode = FindInode(client, server);
        if (inode is null or "0") return false;
        var target = $"socket:[{inode}]";
        foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == target) return true;
            }
            catch (IOException) { /* fd closed meanwhile */ }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------- Windows

    [StructLayout(LayoutKind.Sequential)]
    struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    const int AfInet = 2;
    const int TcpTableOwnerPidConnections = 4;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    static int Port(uint networkOrder) => (int)(((networkOrder & 0xFF) << 8) | ((networkOrder >> 8) & 0xFF));

    static bool Windows(IPEndPoint client, IPEndPoint server)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0) != 0) return false;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            var clientAddr = BitConverter.ToUInt32(client.Address.MapToIPv4().GetAddressBytes());
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(buffer + 4 + i * rowSize);
                if (Port(row.LocalPort) == client.Port && Port(row.RemotePort) == server.Port && row.LocalAddr == clientAddr)
                    return row.OwningPid == (uint)Environment.ProcessId;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Parses "a.b.c.d" + port into an endpoint (SSH.NET reports the originator as text).</summary>
    public static IPEndPoint? Endpoint(string host, uint port) =>
        IPAddress.TryParse(host, out var ip) && port <= ushort.MaxValue ? new IPEndPoint(ip, (int)port) : null;
}
