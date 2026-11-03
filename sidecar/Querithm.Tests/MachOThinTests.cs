using System.Buffers.Binary;
using Querithm.Build;

namespace Querithm.Tests;

/// <summary>Phase D3: a macOS publish keeps only its RID's slice of a universal dylib (sidecar/build/ThinMachO.targets).</summary>
public sealed class MachOThinTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qthin-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>A Mach-O image header (magic, CPU type) followed by filler bytes that identify the slice.</summary>
    static byte[] Image(int cpu, int length, byte fill)
    {
        var b = new byte[length];
        Array.Fill(b, fill);
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFEEDFACF);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), cpu);
        return b;
    }

    /// <summary>A universal file holding <paramref name="slices"/> at 4096-byte aligned offsets (fat_arch or fat_arch_64 entries).</summary>
    static byte[] Fat(bool wide, params (int Cpu, byte[] Image)[] slices)
    {
        var entry = wide ? 32 : 20;
        var offsets = new List<int>();
        var at = 4096;
        foreach (var s in slices) { offsets.Add(at); at += (s.Image.Length + 4095) / 4096 * 4096; }
        var file = new byte[at];
        BinaryPrimitives.WriteUInt32BigEndian(file, wide ? 0xCAFEBABF : 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)slices.Length);
        for (var i = 0; i < slices.Length; i++)
        {
            var e = file.AsSpan(8 + i * entry, entry);
            BinaryPrimitives.WriteInt32BigEndian(e, slices[i].Cpu);
            if (wide)
            {
                BinaryPrimitives.WriteUInt64BigEndian(e[8..], (ulong)offsets[i]);
                BinaryPrimitives.WriteUInt64BigEndian(e[16..], (ulong)slices[i].Image.Length);
                BinaryPrimitives.WriteUInt32BigEndian(e[24..], 12);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)offsets[i]);
                BinaryPrimitives.WriteUInt32BigEndian(e[12..], (uint)slices[i].Image.Length);
                BinaryPrimitives.WriteUInt32BigEndian(e[16..], 12);
            }
            slices[i].Image.CopyTo(file, offsets[i]);
        }
        return file;
    }

    string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Each_macOS_RID_gets_exactly_its_own_slice(bool wide)
    {
        var intel = Image(MachOThin.CpuX86_64, 5000, 0x11);
        var arm = Image(MachOThin.CpuArm64, 7000, 0x22);
        var fat = Write("lib.dylib", Fat(wide, (MachOThin.CpuX86_64, intel), (MachOThin.CpuArm64, arm)));

        Assert.True(MachOThin.TryThin(fat, Path.Combine(_dir, "arm", "lib.dylib"), MachOThin.CpuOf("osx-arm64")!.Value));
        Assert.Equal(arm, File.ReadAllBytes(Path.Combine(_dir, "arm", "lib.dylib")));
        Assert.True(MachOThin.TryThin(fat, Path.Combine(_dir, "intel", "lib.dylib"), MachOThin.CpuOf("osx-x64")!.Value));
        Assert.Equal(intel, File.ReadAllBytes(Path.Combine(_dir, "intel", "lib.dylib")));
    }

    [Fact]
    public void Only_macOS_RIDs_thin_and_a_single_architecture_file_is_left_alone()
    {
        Assert.Null(MachOThin.CpuOf("linux-x64"));
        Assert.Null(MachOThin.CpuOf("win-x64"));
        var thin = Write("thin.dylib", Image(MachOThin.CpuArm64, 4096, 0x33));
        var dest = Path.Combine(_dir, "out.dylib");
        Assert.False(MachOThin.TryThin(thin, dest, MachOThin.CpuArm64));
        Assert.False(File.Exists(dest));
        // A Java class file shares the magic; its version field is no architecture count.
        var cls = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(cls, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(cls.AsSpan(4), 0x00000041);
        Assert.False(MachOThin.TryThin(Write("x.class", cls), dest, MachOThin.CpuArm64));
    }

    [Fact]
    public void A_universal_file_without_the_slice_or_with_a_broken_one_fails_the_publish()
    {
        var dest = Path.Combine(_dir, "out.dylib");
        var intelOnly = Write("intel.dylib", Fat(false, (MachOThin.CpuX86_64, Image(MachOThin.CpuX86_64, 100, 1))));
        Assert.Contains("no slice", Assert.Throws<InvalidDataException>(() => MachOThin.TryThin(intelOnly, dest, MachOThin.CpuArm64)).Message);

        var cut = Fat(false, (MachOThin.CpuArm64, Image(MachOThin.CpuArm64, 6000, 2)));
        Assert.Contains("outside", Assert.Throws<InvalidDataException>(() => MachOThin.TryThin(Write("cut.dylib", cut[..6000]), dest, MachOThin.CpuArm64)).Message);

        // The table says arm64, the image inside says x86_64.
        var lying = Fat(false, (MachOThin.CpuArm64, Image(MachOThin.CpuX86_64, 100, 3)));
        Assert.Contains("not a 64-bit Mach-O", Assert.Throws<InvalidDataException>(() => MachOThin.TryThin(Write("lying.dylib", lying), dest, MachOThin.CpuArm64)).Message);
    }

    [Fact]
    public void The_shipped_DuckDB_dylib_thins_to_one_architecture_per_RID()
    {
        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var dylib = Path.Combine(nuget, "duckdb.net.bindings.full", "1.5.6.1", "runtimes", "osx", "native", "libduckdb.dylib");
        if (!File.Exists(dylib)) return; // restored by every sidecar build; absent only with a custom package folder
        var total = new FileInfo(dylib).Length;
        long sum = 0;
        foreach (var rid in new[] { "osx-arm64", "osx-x64" })
        {
            var dest = Path.Combine(_dir, rid, "libduckdb.dylib");
            Assert.True(MachOThin.TryThin(dylib, dest, MachOThin.CpuOf(rid)!.Value));
            var head = new byte[8];
            using (var f = File.OpenRead(dest)) f.ReadExactly(head);
            Assert.Equal(0xFEEDFACFu, BinaryPrimitives.ReadUInt32LittleEndian(head));
            Assert.Equal(MachOThin.CpuOf(rid), BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4)));
            sum += new FileInfo(dest).Length;
        }
        Assert.True(sum <= total && sum > total * 9 / 10, $"slices {sum:N0} of {total:N0} bytes");
    }
}
