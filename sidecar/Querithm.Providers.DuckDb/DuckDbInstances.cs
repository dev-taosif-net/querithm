using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;
using Querithm.Contract;
using Querithm.Intelligence.DuckDb;

namespace Querithm.Providers.DuckDb;

/// <summary>
/// Opens DuckDB databases under the file and configuration policy (Phase D3, plan "File and configuration policy").
/// DuckDB.NET keeps one native database per Data Source string, process-wide and case-insensitive, and applies settings only
/// when that instance is created; later connections inherit its access mode and settings. DuckDB itself lets one process
/// open the same file twice through two path spellings — two unlocked instances writing one file. So files are opened by
/// grant identity: one Data Source string and one access mode per file while any connection to it is open; a second
/// grant of the same file in another mode, or another file whose path differs only in case, is refused. A fresh instance
/// is configured before any user SQL, in the order DuckDB requires: extension and temp settings, an empty allowlist,
/// enable_external_access = false, lock_configuration = true; then the result is verified. Plain ":memory:" is a new
/// database per connection. Opens of one file are serialised by that file's gate; other files and in-memory databases open
/// meanwhile (L20: a slow open, configure or audit of one file used to hold every DuckDB open).
/// </summary>
public static class DuckDbInstances
{
    sealed class Entry(string dataSource, FileAccessMode mode)
    {
        public string DataSource { get; } = dataSource;
        public FileAccessMode Mode { get; } = mode;
        public List<DuckDBConnection> Connections { get; } = [];
        /// <summary>Opens of this file in flight (waiting for or holding <see cref="Gate"/>); the entry stays while any is.</summary>
        public int Pending;
        // One open of this file at a time: the first connection configures and locks the instance before a second one can
        // inherit it. Other files and in-memory databases never wait on it (L20).
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    // Guards ByIdentity only; never held across a driver call or an await.
    static readonly object Sync = new();
    static readonly Dictionary<string, Entry> ByIdentity = new(StringComparer.Ordinal);

    /// <summary>Test seam (L20): awaited inside a file's gate, before the driver opens it, with the Data Source string.</summary>
    internal static readonly AsyncLocal<Func<string, CancellationToken, Task>?> BeforeOpen = new();

    /// <summary>The memory cap of each instance: a quarter of the memory this process may use, at least 256 MB (DuckDB's default is 80 %).</summary>
    public static long MemoryLimitBytes => Math.Max(256L << 20, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);

    public static async Task<DuckDBConnection> OpenAsync(string? dataDir, FileGrant? file, CancellationToken ct, long? memoryLimitBytes = null)
    {
        // Plain ":memory:" is a database of its own per connection (DuckDB.NET shares no instance for it): nothing to wait for.
        if (file == null) return await OpenConnectionAsync(":memory:", FileAccessMode.ReadOnly, dataDir, memoryLimitBytes, isFile: false, ct).ConfigureAwait(false);
        var entry = Reserve(file);
        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bool first;
                lock (Sync) first = entry.Connections.TrueForAll(c => c.State != System.Data.ConnectionState.Open);
                if (first && new FileInfo(file.Path) is { Exists: true, Length: 0 })
                {
                    if (entry.Mode != FileAccessMode.ReadWrite)
                        throw new InvalidOperationException("The file is empty, so it is not a DuckDB database yet. Allow writes to this file to create a new database in it.");
                    await InitializeEmptyAsync(dataDir, file.Path, ct).ConfigureAwait(false);
                }
                if (BeforeOpen.Value is { } hook) await hook(entry.DataSource, ct).ConfigureAwait(false);
                var c = await OpenConnectionAsync(entry.DataSource, entry.Mode, dataDir, memoryLimitBytes, isFile: true, ct).ConfigureAwait(false);
                lock (Sync) entry.Connections.Add(c);
                return c;
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        finally
        {
            lock (Sync)
            {
                entry.Pending--;
                Prune();
            }
        }
    }

    /// <summary>
    /// The file's entry, made or joined under the short lock: one Data Source string and one access mode per file while any
    /// connection to it is open or opening, and no second file whose path differs only in case (DuckDB.NET's instance cache
    /// ignores case). A new entry is visible before its slow open, so a concurrent open of a case variant is refused too.
    /// </summary>
    static Entry Reserve(FileGrant file)
    {
        lock (Sync)
        {
            Prune();
            if (ByIdentity.TryGetValue(file.Identity, out var entry))
            {
                if (entry.Mode != file.Mode)
                    throw new InvalidOperationException(
                        $"This database file is already open {Describe(entry.Mode)} in another connection, and DuckDB shares one engine per file inside Querithm. " +
                        $"Close the tabs of that connection first, or open the file {Describe(entry.Mode)} here too.");
            }
            else
            {
                if (ByIdentity.Values.Any(e => string.Equals(e.DataSource, file.Path, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        "Another open DuckDB file has the same path in different letter case, and the DuckDB driver would mix the two up. Close that connection first.");
                entry = ByIdentity[file.Identity] = new Entry(file.Path, file.Mode);
            }
            entry.Pending++;
            return entry;
        }
    }

    /// <summary>Opens, configures a fresh instance (an inherited one is already locked), verifies, and audits a file.</summary>
    static async Task<DuckDBConnection> OpenConnectionAsync(string dataSource, FileAccessMode mode, string? dataDir, long? memoryLimitBytes, bool isFile, CancellationToken ct)
    {
        var b = new DuckDBConnectionStringBuilder { DataSource = dataSource };
        if (isFile && mode != FileAccessMode.ReadWrite) b["ACCESS_MODE"] = "READ_ONLY";
        var c = new DuckDBConnection(b.ConnectionString);
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            if (!await LockedAsync(c, ct).ConfigureAwait(false)) await ConfigureAsync(c, dataDir, memoryLimitBytes ?? MemoryLimitBytes, ct).ConfigureAwait(false);
            await VerifyAsync(c, ct).ConfigureAwait(false);
            if (isFile) await AuditAsync(c, ct).ConfigureAwait(false);
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return c;
    }

    static string Describe(FileAccessMode mode) => mode == FileAccessMode.ReadWrite ? "with writes allowed" : "read-only";

    /// <summary>
    /// Forgets files whose connections are all closed and that no open is waiting for: DuckDB.NET disposed their instance
    /// with the last connection. Called under <see cref="Sync"/>.
    /// </summary>
    static void Prune()
    {
        foreach (var (identity, e) in ByIdentity.ToList())
        {
            e.Connections.RemoveAll(c => c.State != System.Data.ConnectionState.Open);
            if (e.Connections.Count == 0 && e.Pending == 0) ByIdentity.Remove(identity);
        }
    }

    /// <summary>Test hook: the files with an open connection, as (identity, data source, mode).</summary>
    internal static IReadOnlyList<(string Identity, string DataSource, FileAccessMode Mode)> Open()
    {
        lock (Sync)
        {
            Prune();
            return ByIdentity.Where(kv => kv.Value.Connections.Count > 0).Select(kv => (kv.Key, kv.Value.DataSource, kv.Value.Mode)).ToList();
        }
    }

    static async Task<bool> LockedAsync(DuckDBConnection c, CancellationToken ct) =>
        string.Equals(await ScalarAsync(c, "SELECT current_setting('lock_configuration')::VARCHAR", ct).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The temp directory of new instances: per process, under app data, removed by DuckDB as its files close.</summary>
    internal static string TempDirectory(string dataDir) =>
        Path.Combine(dataDir, "duckdb", "tmp", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Spill files of sidecars that no longer run (a crash leaves them): removed when this process's provider starts. A
    /// directory is named after the process that owns it.
    /// </summary>
    public static void SweepTemp(string dataDir)
    {
        var root = Path.Combine(dataDir, "duckdb", "tmp");
        if (!Directory.Exists(root)) return;
        foreach (var d in Directory.GetDirectories(root))
        {
            if (!int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == Environment.ProcessId) continue;
            if (Alive(pid)) continue;
            try { Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static bool Alive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// An empty granted file with writes allowed becomes a new database. DuckDB refuses an empty file, so a fresh database
    /// made under app data is written into it — the same file, so the grant's identity still matches (§13 rule 6: the
    /// write stays inside the granted database file).
    /// </summary>
    static async Task InitializeEmptyAsync(string? dataDir, string path, CancellationToken ct)
    {
        if (dataDir == null) throw new InvalidOperationException("DuckDB needs Querithm's data directory to create a database.");
        var temp = TempDirectory(dataDir);
        Directory.CreateDirectory(temp);
        var template = Path.Combine(temp, $"new-{Guid.NewGuid():N}.duckdb");
        try
        {
            using (var c = new DuckDBConnection($"Data Source={template}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CHECKPOINT";
                cmd.ExecuteNonQuery();
            }
            var bytes = await File.ReadAllBytesAsync(template, ct).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            if (target.Length != 0) return; // written in between: leave it to DuckDB's own checks
            await target.WriteAsync(bytes, ct).ConfigureAwait(false);
            await target.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var f in new[] { template, template + ".wal" })
                try { File.Delete(f); } catch (IOException) { }
        }
    }

    static string Literal(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>The policy, in DuckDB's required order (allowlists cannot change once external access is off).</summary>
    static async Task ConfigureAsync(DuckDBConnection c, string? dataDir, long memoryLimit, CancellationToken ct)
    {
        if (dataDir == null) throw new InvalidOperationException("DuckDB needs Querithm's data directory for its temporary files.");
        var temp = TempDirectory(dataDir);
        // DuckDB creates only the last level of its temp directory.
        Directory.CreateDirectory(temp);
        // A directory that is never created: DuckDB must not read or write the user's ~/.duckdb.
        var none = Path.Combine(dataDir, "duckdb", "none");
        var free = 4L << 30;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataDir))!).AvailableFreeSpace; } catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        var maxTemp = Math.Min(10L << 30, Math.Max(1L << 30, free / 4));
        string[] statements =
        [
            "SET GLOBAL autoinstall_known_extensions = false",
            "SET GLOBAL autoload_known_extensions = false",
            "SET GLOBAL allow_community_extensions = false",
            "SET GLOBAL allow_unsigned_extensions = false",
            "SET GLOBAL allow_persistent_secrets = false",
            $"SET GLOBAL extension_directory = {Literal(none)}",
            $"SET GLOBAL secret_directory = {Literal(none)}",
            $"SET GLOBAL temp_directory = {Literal(temp)}",
            $"SET GLOBAL max_temp_directory_size = '{maxTemp.ToString(CultureInfo.InvariantCulture)} bytes'",
            $"SET GLOBAL memory_limit = '{memoryLimit.ToString(CultureInfo.InvariantCulture)} bytes'",
            "SET GLOBAL allowed_directories = []",
            "SET GLOBAL allowed_paths = []",
            "SET GLOBAL enable_external_access = false",
            "SET GLOBAL lock_configuration = true",
        ];
        foreach (var sql in statements)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Refuses the connection unless the instance is locked with external access and extension loading off.</summary>
    static async Task VerifyAsync(DuckDBConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT current_setting('enable_external_access')::VARCHAR, current_setting('lock_configuration')::VARCHAR,
                   current_setting('autoload_known_extensions')::VARCHAR, current_setting('autoinstall_known_extensions')::VARCHAR,
                   current_setting('allow_community_extensions')::VARCHAR
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) throw new NotSupportedException("DuckDB did not report its settings.");
        string[] expected = ["false", "true", "false", "false", "false"];
        for (var i = 0; i < expected.Length; i++)
            if (!string.Equals(r.GetString(i), expected[i], StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"DuckDB would not apply Querithm's file and extension policy ({r.GetName(i)}); the database cannot be opened safely.");
    }

    /// <summary>
    /// An opened file is untrusted: a view or macro that calls a refused function would run it when read, out of the
    /// classifier's sight, so such a file is not opened.
    /// </summary>
    static async Task AuditAsync(DuckDBConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT 'view', schema_name, view_name, sql FROM duckdb_views() WHERE NOT internal AND sql IS NOT NULL
            UNION ALL
            SELECT 'macro', schema_name, function_name, macro_definition FROM duckdb_functions() WHERE NOT internal AND macro_definition IS NOT NULL
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            if (DuckDbLanguageService.RefusedCall(r.GetString(3)) is { } fn)
                throw new NotSupportedException(
                    $"This database's {r.GetString(0)} {r.GetString(1)}.{r.GetString(2)} calls {fn}(), which Querithm refuses (it writes logs, prints to the engine's output, reads raw memory or runs unchecked SQL). The file is not opened.");
    }

    static async Task<string?> ScalarAsync(DbConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}
