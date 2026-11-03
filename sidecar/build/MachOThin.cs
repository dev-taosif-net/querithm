// Phase D3: DuckDB.NET ships one universal libduckdb.dylib (x86_64 + arm64, 111.6 MB) for both macOS RIDs, and each RID
// runs only its own slice. Thinning is the byte copy `lipo -thin` makes (every slice carries its own code signature), done
// here so that a publish on any OS thins: ThinMachO.targets runs it on the publish list, Querithm.Tests links this file.
// MSBuild compiles the task against netstandard: no spans, BinaryPrimitives or implicit usings here.
using System;
using System.Collections.Generic;
using System.IO;

namespace Querithm.Build;

public static class MachOThin
{
    public const int CpuX86_64 = 0x01000007;
    public const int CpuArm64 = 0x0100000C;
    const uint FatMagic = 0xCAFEBABE, FatMagic64 = 0xCAFEBABF, MachMagic64 = 0xFEEDFACF;

    /// <summary>The CPU type a macOS RID runs, or null for any other RID.</summary>
    public static int? CpuOf(string rid) => rid == "osx-arm64" ? CpuArm64 : rid == "osx-x64" ? CpuX86_64 : null;

    /// <summary>
    /// Writes the <paramref name="cpuType"/> slice of the universal Mach-O <paramref name="source"/> to
    /// <paramref name="destination"/>. False when the source is not a universal file (nothing is written); throws
    /// <see cref="InvalidDataException"/> when it is one without that slice or with a slice that does not fit the file.
    /// </summary>
    public static bool TryThin(string source, string destination, int cpuType)
    {
        using var input = File.OpenRead(source);
        var head = new byte[8];
        if (!ReadFull(input, head, 8)) return false;
        var magic = BigEndian32(head, 0);
        if (magic != FatMagic && magic != FatMagic64) return false;
        var count = BigEndian32(head, 4);
        // A Java class file shares the magic; its next field is a version number, never a small architecture count.
        if (count == 0 || count > 16) return false;
        var entry = magic == FatMagic64 ? 32 : 20;
        var table = new byte[count * entry];
        if (!ReadFull(input, table, table.Length)) throw new InvalidDataException($"{source}: the universal header is cut short.");
        for (var i = 0; i < count; i++)
        {
            var e = i * entry;
            if ((int)BigEndian32(table, e) != cpuType) continue;
            long offset = magic == FatMagic64 ? (long)BigEndian64(table, e + 8) : BigEndian32(table, e + 8);
            long size = magic == FatMagic64 ? (long)BigEndian64(table, e + 16) : BigEndian32(table, e + 12);
            if (offset < 8 + table.Length || size < 8 || offset + size > input.Length)
                throw new InvalidDataException($"{source}: slice {i} ({offset} + {size} bytes) lies outside the {input.Length}-byte file.");
            input.Position = offset;
            if (!ReadFull(input, head, 8) || LittleEndian32(head, 0) != MachMagic64 || (int)LittleEndian32(head, 4) != cpuType)
                throw new InvalidDataException($"{source}: slice {i} is not a 64-bit Mach-O image of CPU type 0x{cpuType:X8}.");
            input.Position = offset;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            using var output = File.Create(destination);
            var buffer = new byte[1 << 20];
            for (var left = size; left > 0;)
            {
                var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) throw new InvalidDataException($"{source}: slice {i} ends early.");
                output.Write(buffer, 0, n);
                left -= n;
            }
            return true;
        }
        throw new InvalidDataException($"{source}: the universal file has no slice for CPU type 0x{cpuType:X8}.");
    }

    static bool ReadFull(Stream s, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = s.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    static uint BigEndian32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
    static ulong BigEndian64(byte[] b, int at) => (ulong)BigEndian32(b, at) << 32 | BigEndian32(b, at + 4);
    static uint LittleEndian32(byte[] b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);
}

#if !QUERITHM_TESTS
/// <summary>Replaces each universal .dylib on a macOS publish list with its slice for the RID (metadata kept).</summary>
public sealed class ThinMachOLibraries : Microsoft.Build.Utilities.Task
{
    [Microsoft.Build.Framework.Required] public Microsoft.Build.Framework.ITaskItem[] Files { get; set; } = new Microsoft.Build.Framework.ITaskItem[0];
    [Microsoft.Build.Framework.Required] public string RuntimeIdentifier { get; set; } = "";
    [Microsoft.Build.Framework.Required] public string OutputDirectory { get; set; } = "";
    [Microsoft.Build.Framework.Output] public Microsoft.Build.Framework.ITaskItem[] Replaced { get; set; } = new Microsoft.Build.Framework.ITaskItem[0];
    [Microsoft.Build.Framework.Output] public Microsoft.Build.Framework.ITaskItem[] Thinned { get; set; } = new Microsoft.Build.Framework.ITaskItem[0];

    public override bool Execute()
    {
        var cpu = MachOThin.CpuOf(RuntimeIdentifier);
        if (cpu == null) return true;
        var replaced = new List<Microsoft.Build.Framework.ITaskItem>();
        var thinned = new List<Microsoft.Build.Framework.ITaskItem>();
        foreach (var f in Files)
        {
            var destination = Path.Combine(OutputDirectory, RuntimeIdentifier, Path.GetFileName(f.ItemSpec));
            try
            {
                if (!MachOThin.TryThin(f.ItemSpec, destination, cpu.Value)) continue;
            }
            catch (InvalidDataException e)
            {
                Log.LogError(e.Message);
                return false;
            }
            var item = new Microsoft.Build.Utilities.TaskItem(Path.GetFullPath(destination));
            f.CopyMetadataTo(item);
            replaced.Add(f);
            thinned.Add(item);
            Log.LogMessage(Microsoft.Build.Framework.MessageImportance.High,
                $"{Path.GetFileName(f.ItemSpec)}: {new FileInfo(f.ItemSpec).Length:N0} -> {new FileInfo(destination).Length:N0} bytes ({RuntimeIdentifier} slice)");
        }
        Replaced = replaced.ToArray();
        Thinned = thinned.ToArray();
        return true;
    }
}
#endif
