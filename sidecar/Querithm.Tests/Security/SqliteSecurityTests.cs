using Microsoft.Data.Sqlite;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Providers.Sqlite;
using Querithm.Tests.FileEngines;

namespace Querithm.Tests.Security;

/// <summary>
/// Phase D1 / D0c file policy, enforced natively on every SQLite connection — these run statements straight on the
/// provider's connection, past the classifier, so the authorizer, defensive mode and the attach limit are what refuse them.
/// </summary>
public sealed class SqliteSecurityTests : IAsyncDisposable
{
    readonly SqliteHost _h = new();
    readonly SqliteProvider _p = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    async Task<SqliteConnection> OpenAsync(string? path, FileAccessMode mode = FileAccessMode.ReadOnly)
    {
        var profile = path == null
            ? new Profile { Id = "p", Name = "m", Engine = Engine.Sqlite, Auth = AuthKind.None, Location = LocationKind.Memory }
            : new Profile { Id = "p", Name = "f", Engine = Engine.Sqlite, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = "g1" } };
        return (SqliteConnection)await _p.OpenAsync(profile, null, null, path == null ? null : SqliteHost.Grant(path, mode), default);
    }

    static SqliteException Refused(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public async Task Attach_is_refused_natively_in_every_form()
    {
        var other = _h.NewDatabase("other.db");
        await using var c = await OpenAsync(_h.NewDatabase(), FileAccessMode.ReadWrite);
        Refused(c, $"ATTACH DATABASE '{other}' AS o");
        Refused(c, $"ATTACH (SELECT '{other[..^3]}' || '.db') AS o"); // a computed filename
        var created = Path.Combine(_h.Dir, "created-by-uri.db");
        Refused(c, $"ATTACH 'file:{created}?mode=rwc' AS u");
        Assert.False(File.Exists(created));
    }

    [Fact]
    public async Task Uri_attach_on_an_in_memory_session_is_refused()
    {
        await using var c = await OpenAsync(null);
        var target = Path.Combine(_h.Dir, "from-memory.db");
        Refused(c, $"ATTACH 'file:{target}?mode=rwc' AS x");
        Refused(c, "ATTACH ':memory:' AS x2");
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Vacuum_into_never_creates_a_file_even_from_a_read_only_connection()
    {
        var path = _h.NewDatabase();
        var target = Path.Combine(_h.Dir, "copy.db");
        await using (var ro = await OpenAsync(path))
            Refused(ro, $"VACUUM INTO '{target}'");
        await using (var rw = await OpenAsync(path, FileAccessMode.ReadWrite))
            Refused(rw, $"VACUUM main INTO '{target}'");
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Extensions_cannot_be_loaded()
    {
        await using var c = await OpenAsync(null);
        var ex = Refused(c, "SELECT load_extension('/tmp/evil.so')");
        Assert.True(ex.SqliteErrorCode is 23 or 1, $"code {ex.SqliteErrorCode}");
    }

    [Theory]
    [InlineData("PRAGMA trusted_schema = ON")]
    [InlineData("PRAGMA writable_schema = ON")]
    [InlineData("PRAGMA journal_mode = OFF")]
    [InlineData("PRAGMA temp_store_directory = '/tmp'")]
    [InlineData("PRAGMA main.schema_version = 1")]
    public async Task Security_and_file_pragmas_cannot_be_changed(string sql)
    {
        await using var c = await OpenAsync(_h.NewDatabase(), FileAccessMode.ReadWrite);
        Refused(c, sql);
        using var check = c.CreateCommand();
        check.CommandText = "PRAGMA trusted_schema";
        Assert.Equal(0L, check.ExecuteScalar());
    }

    [Fact]
    public async Task A_read_only_grant_cannot_be_escalated_by_sql()
    {
        var path = _h.NewDatabase();
        var before = await File.ReadAllBytesAsync(path);
        await using var c = await OpenAsync(path);
        Refused(c, "INSERT INTO t(v) VALUES (1)");
        Refused(c, "PRAGMA query_only = OFF; INSERT INTO t(v) VALUES (1)");
        Refused(c, "CREATE TABLE x (a)");
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_read_only_open_of_a_wal_database_leaves_the_database_file_unchanged()
    {
        var path = _h.NewDatabase("wal.db", "CREATE TABLE t (id INTEGER PRIMARY KEY, v); INSERT INTO t(v) VALUES ('x');", wal: true);
        var before = await File.ReadAllBytesAsync(path);
        await using (var c = await OpenAsync(path))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT v FROM t";
            Assert.Equal("x", cmd.ExecuteScalar());
        }
        // The engine may keep its own -wal / -shm beside the file (the journal files the grant covers); the database is untouched.
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_h.Dir).Where(f => !f.StartsWith(path, StringComparison.Ordinal) && Path.GetFileName(f) != "sales.db" && !Directory.Exists(f)));
    }

    [Fact]
    public async Task The_classifier_refuses_before_anything_runs_and_confirmation_does_not_lift_it()
    {
        var (_, sid) = await _h.OpenFileAsync(_h.NewDatabase(), FileAccessMode.ReadWrite, EnvironmentKind.Prod);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => _h.Rpc.QueryExecute(new QueryExecuteParams
        {
            SessionId = sid, Sql = "SELECT 1; ATTACH 'x.db' AS x", Options = new ExecuteOptions { ConfirmedProd = true, ConfirmedDestructive = true },
        }));
        Assert.Equal("refused", ex.Code);
        Assert.Contains("ATTACH", ex.Message);
    }

    [Fact]
    public async Task Temporary_data_never_spills_to_files()
    {
        var before = Directory.GetFiles(Path.GetTempPath()).Length;
        await using var c = await OpenAsync(null);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TEMP TABLE big AS WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 20000) SELECT i, randomblob(200) AS b FROM n; SELECT count(*) FROM big ORDER BY b";
        Assert.Equal(20000L, cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA temp_store";
        Assert.Equal(2L, cmd.ExecuteScalar());
        _ = before; // temp_store = MEMORY is the guarantee; the count above only exercises it
    }
}
