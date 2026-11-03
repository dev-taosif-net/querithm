using DuckDB.NET.Data;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Providers.DuckDb;
using Querithm.Tests.FileEngines;

namespace Querithm.Tests.Security;

/// <summary>
/// Phase D3 file and configuration policy. The engine-level tests run statements straight on the provider's connection,
/// past the classifier, so DuckDB's own locked configuration is what refuses them; the classifier tests go through
/// query.execute, for what the engine allows but Querithm refuses (pragmas that print to the sidecar's stdout / stderr,
/// logging that aborts the process, COPY TO the database's own files).
/// </summary>
public sealed class DuckDbSecurityTests : IAsyncDisposable
{
    readonly DuckDbHost _h = new();
    readonly DuckDbProvider _p;

    public DuckDbSecurityTests() => _p = new DuckDbProvider(_h.AppData);

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    async Task<DuckDBConnection> OpenAsync(string? path, FileAccessMode mode = FileAccessMode.ReadOnly, string grantId = "g1")
    {
        var profile = path == null
            ? new Profile { Id = "p", Name = "m", Engine = Engine.DuckDb, Auth = AuthKind.None, Location = LocationKind.Memory }
            : new Profile { Id = "p", Name = "f", Engine = Engine.DuckDb, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = grantId } };
        return (DuckDBConnection)await _p.OpenAsync(profile, null, null, path == null ? null : DuckDbHost.Grant(path, mode, grantId), default);
    }

    static DuckDBException Refused(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Assert.Throws<DuckDBException>(() => cmd.ExecuteNonQuery());
    }

    static string? Scalar(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    string Q(string path) => "'" + path.Replace("'", "''") + "'";

    [Fact]
    public async Task Every_connection_is_locked_with_external_access_and_extension_loading_off_before_user_sql()
    {
        await using var mem = await OpenAsync(null);
        await using var file = await OpenAsync(_h.NewDatabase());
        foreach (var c in new[] { mem, file })
        {
            Assert.Equal("false", Scalar(c, "SELECT current_setting('enable_external_access')::VARCHAR"));
            Assert.Equal("true", Scalar(c, "SELECT current_setting('lock_configuration')::VARCHAR"));
            Assert.Equal("false", Scalar(c, "SELECT current_setting('autoload_known_extensions')::VARCHAR"));
            Assert.Equal("false", Scalar(c, "SELECT current_setting('autoinstall_known_extensions')::VARCHAR"));
            Assert.Equal("false", Scalar(c, "SELECT current_setting('allow_community_extensions')::VARCHAR"));
            Assert.StartsWith(_h.AppData, Scalar(c, "SELECT current_setting('temp_directory')"));
            Assert.InRange(MemoryBytes(Scalar(c, "SELECT current_setting('memory_limit')")!), 0.95 * DuckDbInstances.MemoryLimitBytes, 1.05 * DuckDbInstances.MemoryLimitBytes);
        }
    }

    /// <summary>DuckDB prints memory_limit as "3.5 GiB".</summary>
    static double MemoryBytes(string text)
    {
        var parts = text.Split(' ');
        var n = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        return n * parts[1] switch { "KiB" => 1L << 10, "MiB" => 1L << 20, "GiB" => 1L << 30, "TiB" => 1L << 40, _ => 1 };
    }

    [Fact]
    public async Task Sql_cannot_reenable_external_access_widen_paths_or_unlock_the_configuration()
    {
        await using var c = await OpenAsync(_h.NewDatabase(), FileAccessMode.ReadWrite);
        Refused(c, "SET enable_external_access = true");
        Refused(c, "SET GLOBAL enable_external_access = true");
        Refused(c, "RESET enable_external_access");
        Refused(c, $"SET allowed_directories = [{Q(_h.Dir)}]");
        Refused(c, $"SET allowed_paths = [{Q(_h.Dir)}]");
        Refused(c, "SET lock_configuration = false");
        Refused(c, "RESET lock_configuration");
        Refused(c, "SET autoload_known_extensions = true");
        Refused(c, $"SET temp_directory = {Q(_h.Dir)}");
        Refused(c, "SET memory_limit = '100GB'");
        Assert.Equal("false", Scalar(c, "SELECT current_setting('enable_external_access')::VARCHAR"));
    }

    [Fact]
    public async Task Extensions_cannot_be_installed_or_loaded_and_urls_are_refused()
    {
        await using var c = await OpenAsync(null);
        Refused(c, "INSTALL httpfs");
        Refused(c, "FORCE INSTALL spatial");
        Refused(c, "LOAD spatial");
        Refused(c, "SELECT * FROM read_parquet('https://example.invalid/x.parquet')");
        Refused(c, "SELECT * FROM read_csv('s3://bucket/x.csv')");
        Assert.False(Directory.Exists(Path.Combine(_h.AppData, "duckdb", "none")));
    }

    [Fact]
    public async Task Files_outside_the_database_are_neither_read_nor_written_nor_attached()
    {
        var outside = Path.Combine(_h.Dir, "outside.csv");
        await File.WriteAllTextAsync(outside, "x\n1\n");
        var other = _h.NewDatabase("other.duckdb", "CREATE TABLE o (x INT)");
        await using var c = await OpenAsync(_h.NewDatabase(), FileAccessMode.ReadWrite);
        Refused(c, $"SELECT * FROM read_csv({Q(outside)})");
        Refused(c, $"SELECT * FROM read_text({Q(outside)})");
        Refused(c, $"SELECT * FROM glob({Q(Path.Combine(_h.Dir, "*"))})");
        Refused(c, $"ATTACH {Q(other)} AS o");
        Refused(c, $"ATTACH {Q(Path.Combine(_h.Dir, "new.duckdb"))} AS n");
        Refused(c, $"COPY t TO {Q(Path.Combine(_h.Dir, "out.csv"))}");
        Refused(c, $"EXPORT DATABASE {Q(Path.Combine(_h.Dir, "export"))}");
        Refused(c, $"COPY t FROM {Q(outside)}");
        Assert.False(File.Exists(Path.Combine(_h.Dir, "new.duckdb")));
        Assert.False(File.Exists(Path.Combine(_h.Dir, "out.csv")));
        Assert.False(Directory.Exists(Path.Combine(_h.Dir, "export")));
    }

    [Fact]
    public async Task Secrets_and_profiler_output_paths_are_refused_by_the_engine()
    {
        await using var c = await OpenAsync(null);
        Refused(c, "CREATE SECRET s (TYPE s3, KEY_ID 'a', SECRET 'b')");
        Refused(c, "CREATE PERSISTENT SECRET s (TYPE s3, KEY_ID 'a', SECRET 'b')");
        Refused(c, $"SET profiling_output = {Q(Path.Combine(_h.Dir, "p.json"))}");
        Refused(c, $"PRAGMA profiling_output = {Q(Path.Combine(_h.Dir, "p.json"))}");
        Refused(c, "SET enable_progress_bar = true");
        Assert.False(File.Exists(Path.Combine(_h.Dir, "p.json")));
    }

    [Fact]
    public async Task A_read_only_profile_rejects_writes_and_leaves_the_file_unchanged()
    {
        var path = _h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v VARCHAR); INSERT INTO t VALUES (1, 'a');");
        var before = await File.ReadAllBytesAsync(path);
        await using (var c = await OpenAsync(path))
        {
            Assert.Equal("1", Scalar(c, "SELECT count(*) FROM t"));
            Refused(c, "INSERT INTO t VALUES (2, 'b')");
            Refused(c, "CREATE TABLE x (a INT)");
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".wal"));
    }

    [Fact]
    public async Task Two_grants_of_one_file_in_different_modes_are_refused_while_either_is_open()
    {
        var path = _h.NewDatabase();
        await using (var rw = await OpenAsync(path, FileAccessMode.ReadWrite))
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(path, FileAccessMode.ReadOnly, "g2"));
            Assert.Contains("already open with writes allowed", e.Message);
            // The same mode shares the instance (and its locked settings).
            await using var again = await OpenAsync(path, FileAccessMode.ReadWrite, "g2");
            Assert.Equal("true", Scalar(again, "SELECT current_setting('lock_configuration')::VARCHAR"));
        }
        // Once every connection is closed the instance is gone and the other mode opens.
        await using var ro = await OpenAsync(path);
        Refused(ro, "INSERT INTO t VALUES (1, 'x')");
    }

    [Fact]
    public async Task A_path_alias_of_an_open_file_is_the_same_file_and_cannot_change_its_mode()
    {
        var path = _h.NewDatabase();
        var alias = Path.Combine(_h.Dir, ".", Path.GetFileName(path));
        await using var rw = await OpenAsync(path, FileAccessMode.ReadWrite);
        // A different spelling would give DuckDB a second, unlocked instance on the same file: the grant identity decides.
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(alias, FileAccessMode.ReadOnly, "g2"));
        Assert.Contains("already open", e.Message);
        await using var same = await OpenAsync(alias, FileAccessMode.ReadWrite, "g3");
        Assert.Equal("false", Scalar(same, "SELECT current_setting('enable_external_access')::VARCHAR"));
        Assert.Single(DuckDbInstances.Open(), o => o.DataSource == path);
    }

    [Fact]
    public async Task Another_file_whose_path_differs_only_in_case_is_refused_while_the_first_is_open()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return; // case-insensitive file systems: one file
        var lower = _h.NewDatabase("case.duckdb");
        var upper = _h.NewDatabase("CASE.duckdb");
        await using var first = await OpenAsync(lower, FileAccessMode.ReadWrite);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(upper, FileAccessMode.ReadWrite, "g2"));
        Assert.Contains("letter case", e.Message);
    }

    [Fact]
    public async Task A_file_whose_views_or_macros_call_refused_functions_is_not_opened()
    {
        var path = _h.NewDatabase("trap.duckdb", "CREATE VIEW v AS SELECT * FROM enable_logging(level := 'trace');");
        var e = await Assert.ThrowsAsync<NotSupportedException>(() => OpenAsync(path));
        Assert.Contains("enable_logging", e.Message);
        var macro = _h.NewDatabase("trap2.duckdb", "CREATE MACRO m() AS TABLE SELECT * FROM \"query\"('SELECT 1');");
        e = await Assert.ThrowsAsync<NotSupportedException>(() => OpenAsync(macro));
        Assert.Contains("query", e.Message);
        Assert.DoesNotContain(DuckDbInstances.Open(), o => o.DataSource == path || o.DataSource == macro);
    }

    [Fact]
    public async Task Spill_files_land_under_app_data()
    {
        // A 256 MB cap makes a sort of ~600 MB spill; the profile reports the temp directory's peak size.
        var small = new DuckDbProvider(_h.AppData, 256L << 20);
        var profile = new Profile { Id = "p", Name = "m", Engine = Engine.DuckDb, Auth = AuthKind.None, Location = LocationKind.Memory };
        await using var c = (DuckDBConnection)await small.OpenAsync(profile, null, null, null, default);
        Assert.StartsWith(DuckDbInstances.TempDirectory(_h.AppData), Scalar(c, "SELECT current_setting('temp_directory')"));
        using var cmd = c.CreateCommand();
        cmd.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) SELECT * FROM (SELECT range AS k, md5(range::VARCHAR) || md5((range + 1)::VARCHAR) AS v FROM range(6000000)) ORDER BY v";
        string json;
        using (var r = cmd.ExecuteReader())
        {
            Assert.True(r.Read());
            json = r.GetString(1);
        }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("system_peak_temp_dir_size").GetInt64() > 0, "the sort did not spill");
        Assert.False(Directory.Exists(".tmp") && Directory.EnumerateFileSystemEntries(".tmp").Any());
    }

    [Fact]
    public void The_temp_sweep_removes_only_directories_of_processes_that_no_longer_run()
    {
        var root = Path.Combine(_h.AppData, "duckdb", "tmp");
        var dead = Directory.CreateDirectory(Path.Combine(root, "999999999")).FullName;
        File.WriteAllText(Path.Combine(dead, "duckdb_temp_block-1.block"), "x");
        var mine = Directory.CreateDirectory(Path.Combine(root, Environment.ProcessId.ToString())).FullName;
        var other = Directory.CreateDirectory(Path.Combine(root, "not-a-pid")).FullName;
        DuckDbInstances.SweepTemp(_h.AppData);
        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(mine));
        Assert.True(Directory.Exists(other));
    }

    [Theory]
    [InlineData("PRAGMA enable_progress_bar")]
    [InlineData("PRAGMA enable_print_progress_bar")]
    [InlineData("PRAGMA enable_profiling")]
    [InlineData("PRAGMA enable_profile")]
    [InlineData("PRAGMA enable_verification")]
    [InlineData("PRAGMA disable_checkpoint_on_shutdown")]
    [InlineData("CALL enable_logging()")]
    [InlineData("CALL enable_logging(storage_path := '/tmp/x.log')")]
    [InlineData("SELECT * FROM enable_logging()")]
    [InlineData("SELECT * FROM system.main.enable_logging()")]
    [InlineData("SELECT * FROM \"enable_logging\"()")]
    [InlineData("CALL disable_logging()")]
    [InlineData("CALL truncate_duckdb_logs()")]
    [InlineData("SELECT * FROM query('SELECT 1')")]
    [InlineData("SELECT * FROM query_table('t')")]
    [InlineData("SELECT * FROM arrow_scan(0, 0, 0)")]
    [InlineData("SELECT * FROM json_execute_serialized_sql('x')")]
    [InlineData("COPY t TO 'x.csv'")]
    [InlineData("COPY (SELECT 1) TO 'x.parquet' (FORMAT parquet)")]
    [InlineData("COPY t FROM 'x.csv'")]
    [InlineData("COPY FROM DATABASE a TO b")]
    [InlineData("EXPORT DATABASE 'dir'")]
    [InlineData("IMPORT DATABASE 'dir'")]
    [InlineData("ATTACH ':memory:' AS m")]
    [InlineData("DETACH m")]
    [InlineData("INSTALL httpfs")]
    [InlineData("LOAD json")]
    [InlineData("CREATE SECRET (TYPE s3)")]
    [InlineData("CREATE OR REPLACE PERSISTENT SECRET s (TYPE s3)")]
    [InlineData("DROP SECRET s")]
    [InlineData("SET threads = 1")]
    [InlineData("SET GLOBAL memory_limit = '1GB'")]
    [InlineData("RESET enable_external_access")]
    [InlineData("PRAGMA memory_limit = '1GB'")]
    [InlineData("EXPLAIN ANALYZE COPY t TO 'x.csv'")]
    public async Task The_sidecar_refuses_what_the_engine_would_allow_or_what_no_grant_covers(string sql)
    {
        var (_, sid) = await _h.OpenFileAsync(_h.NewDatabase(), FileAccessMode.ReadWrite);
        var e = await Assert.ThrowsAsync<QuerithmException>(() => _h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = sql }));
        Assert.Equal("refused", e.Code);
    }

    [Theory]
    [InlineData("SET schema = 'main'")]
    [InlineData("SET search_path = 'main'")]
    [InlineData("RESET schema")]
    [InlineData("SET VARIABLE x = 1")]
    [InlineData("PRAGMA table_info('t')")]
    [InlineData("PRAGMA database_size")]
    [InlineData("PRAGMA show_tables")]
    [InlineData("CHECKPOINT")]
    [InlineData("CREATE TABLE query (a INT)")]
    [InlineData("SELECT query FROM (SELECT 1 AS query)")]
    public async Task Ordinary_statements_that_only_look_alike_still_run(string sql)
    {
        var (_, sid) = await _h.OpenFileAsync(_h.NewDatabase(), FileAccessMode.ReadWrite);
        var (done, _, _) = await _h.RunAsync(sid, sql);
        Assert.Null(done.Error);
    }

    [Fact]
    public async Task Copy_to_the_database_file_itself_is_refused_before_it_reaches_the_engine()
    {
        var path = _h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v VARCHAR); INSERT INTO t VALUES (1, 'a');");
        var (_, sid) = await _h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        foreach (var target in new[] { path, path + ".wal" })
        {
            var e = await Assert.ThrowsAsync<QuerithmException>(() => _h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = $"COPY t TO {Q(target)}" }));
            Assert.Equal("refused", e.Code);
        }
        var (q, b, _) = await _h.RunAsync(sid, "SELECT count(*) AS n FROM t");
        Assert.Null(q.Error);
    }
}
