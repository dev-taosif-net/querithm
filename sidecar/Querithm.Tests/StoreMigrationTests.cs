using Microsoft.Data.Sqlite;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Store;

namespace Querithm.Tests;

/// <summary>Schema migrations of store.db files written by older builds (§8): one transaction, data kept.</summary>
public class StoreMigrationTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qstore-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    static SqliteConnection Raw(string path)
    {
        var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        return c;
    }

    static object? Scalar(string path, string sql)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    static void Exec(string path, string sql)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static bool HasColumn(string path, string table, string column) =>
        (long)Scalar(path, $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = '{column}'")! == 1;

    static bool HasTable(string path, string table) =>
        (long)Scalar(path, $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'")! == 1;

    /// <summary>A file as build 0.1.6 wrote it (user_version 1: obj without parent_object_id, no snippet table), with rows.</summary>
    string V1File()
    {
        var path = Path.Combine(_dir, $"v1-{Guid.NewGuid():N}.db");
        Exec(path, """
            CREATE TABLE schema_snapshot (profile_id TEXT, database TEXT, refreshed_at INTEGER, server_version TEXT, status TEXT, error TEXT, PRIMARY KEY (profile_id, database));
            CREATE TABLE obj (profile_id TEXT, database TEXT, object_id INTEGER, schema_name TEXT, name TEXT, kind TEXT,
              modify_date INTEGER, definition_hash TEXT, return_type TEXT, PRIMARY KEY (profile_id, database, object_id));
            CREATE TABLE setting (key TEXT PRIMARY KEY, value_json TEXT);
            INSERT INTO schema_snapshot VALUES ('p', 'db', 1000, '16', 'fresh', NULL);
            INSERT INTO obj VALUES ('p', 'db', 1, 'dbo', 'Orders', 'table', 5, NULL, NULL);
            INSERT INTO setting VALUES ('theme', '"dark"');
            PRAGMA user_version = 1;
            """);
        return path;
    }

    [Fact]
    public void A_fresh_store_is_at_the_current_version_with_every_table()
    {
        var path = Path.Combine(_dir, "fresh.db");
        _ = new LocalStore(path);
        Assert.Equal((long)LocalStore.Version, Scalar(path, "PRAGMA user_version"));
        Assert.True(HasColumn(path, "obj", "parent_object_id"));
        Assert.True(HasTable(path, "snippet"));
    }

    [Fact]
    public void A_v1_file_keeps_its_rows_gains_the_trigger_column_and_its_snapshots_go_stale()
    {
        var path = V1File();
        var store = new LocalStore(path);
        Assert.Equal((long)LocalStore.Version, Scalar(path, "PRAGMA user_version"));
        Assert.True(HasColumn(path, "obj", "parent_object_id"));
        Assert.True(HasTable(path, "snippet"));
        // Triggers were never read into these snapshots, so the next open refreshes them.
        Assert.Equal("stale", Scalar(path, "SELECT status FROM schema_snapshot WHERE profile_id = 'p'"));
        Assert.Equal("\"dark\"", Scalar(path, "SELECT value_json FROM setting WHERE key = 'theme'"));
        var snap = new MetadataStore(store).Load("p", "db");
        var o = Assert.Single(snap.Objects);
        Assert.Equal(("Orders", ObjectKind.Table, (long?)null), (o.Name, o.Kind, o.ParentObjectId));
        Assert.Equal(SnapshotStatus.Stale, snap.Status);
    }

    [Fact]
    public void A_v2_file_gains_the_snippet_table_and_keeps_trigger_parents()
    {
        var path = V1File();
        Exec(path, """
            ALTER TABLE obj ADD COLUMN parent_object_id INTEGER;
            INSERT INTO obj VALUES ('p', 'db', 2, 'dbo', 'trg_Orders', 'trigger', 7, NULL, NULL, 1);
            PRAGMA user_version = 2;
            """);
        var store = new LocalStore(path);
        Assert.Equal((long)LocalStore.Version, Scalar(path, "PRAGMA user_version"));
        Assert.True(HasTable(path, "snippet"));
        // Only the v1 → v2 step marks snapshots stale.
        Assert.Equal("fresh", Scalar(path, "SELECT status FROM schema_snapshot WHERE profile_id = 'p'"));
        var trigger = new MetadataStore(store).Load("p", "db").Objects.Single(x => x.Kind == ObjectKind.Trigger);
        Assert.Equal(1L, trigger.ParentObjectId);
    }

    [Fact]
    public void A_current_file_reset_to_v1_by_an_older_build_opens_again()
    {
        // Builds up to 0.1.6 set user_version = 1 on every open, whatever the file already held.
        var path = Path.Combine(_dir, "downgraded.db");
        _ = new LocalStore(path);
        Exec(path, """
            INSERT INTO schema_snapshot (profile_id, database, refreshed_at, server_version, status) VALUES ('p', 'db', 1000, '16', 'fresh');
            PRAGMA user_version = 1;
            """);
        _ = new LocalStore(path);
        Assert.Equal((long)LocalStore.Version, Scalar(path, "PRAGMA user_version"));
        Assert.True(HasColumn(path, "obj", "parent_object_id"));
        Assert.Equal("stale", Scalar(path, "SELECT status FROM schema_snapshot WHERE profile_id = 'p'"));
    }

    [Fact]
    public void A_migration_that_fails_midway_changes_nothing_and_the_next_open_completes_it()
    {
        var path = V1File();
        LocalStore.AfterMigrationStep = (p, to) => { if (p == path && to == 3) throw new IOException("power cut"); };
        try
        {
            Assert.Throws<IOException>(() => new LocalStore(path));
        }
        finally { LocalStore.AfterMigrationStep = null; }
        // Step 2 had run inside the transaction; the rollback undid it.
        Assert.Equal(1L, Scalar(path, "PRAGMA user_version"));
        Assert.False(HasColumn(path, "obj", "parent_object_id"));
        Assert.Equal("fresh", Scalar(path, "SELECT status FROM schema_snapshot WHERE profile_id = 'p'"));

        var store = new LocalStore(path);
        Assert.Equal((long)LocalStore.Version, Scalar(path, "PRAGMA user_version"));
        Assert.True(HasColumn(path, "obj", "parent_object_id"));
        Assert.Equal("Orders", Assert.Single(new MetadataStore(store).Load("p", "db").Objects).Name);
    }

    [Fact]
    public void A_v4_file_gains_the_history_preview_columns_and_its_rows_are_previewed_on_read()
    {
        // L17: history rows written before v5 have no preview or length; list and search cut them on read.
        var path = Path.Combine(_dir, "v4.db");
        _ = new LocalStore(path);
        var longSql = "SELECT " + new string('a', 2_000);
        Exec(path, $"""
            ALTER TABLE history DROP COLUMN preview;
            ALTER TABLE history DROP COLUMN sql_length;
            INSERT INTO history (profile_id, database, executed_at, sql, elapsed_ms, rows_affected, status, error) VALUES ('p', 'db', 1, '{longSql}', 1, NULL, 'done', NULL);
            INSERT INTO history (profile_id, database, executed_at, sql, elapsed_ms, rows_affected, status, error) VALUES ('p', 'db', 2, 'SELECT 1', 1, NULL, 'done', NULL);
            PRAGMA user_version = 4;
            """);
        SqliteConnection.ClearAllPools();
        var store = new LocalStore(path);
        Assert.Equal((long)LocalStore.Version, Scalar(path, "PRAGMA user_version"));
        Assert.True(HasColumn(path, "history", "preview"));
        Assert.True(HasColumn(path, "history", "sql_length"));
        var history = new HistoryStore(store);
        var rows = history.List(new HistoryListParams());
        var old = rows.Single(h => h.ExecutedAt == 1);
        Assert.Equal((HistoryLimits.PreviewChars, true, (long)longSql.Length), (old.Sql.Length, old.SqlTruncated, old.SqlLength));
        Assert.Equal(longSql, history.Get(old.Id).Sql);
        Assert.False(rows.Single(h => h.ExecutedAt == 2).SqlTruncated);
        // A new row stores its preview.
        var id = history.Add("p", "db", longSql, 1, null, "done", null);
        Assert.Equal((long)HistoryLimits.PreviewChars, Scalar(path, $"SELECT length(preview) FROM history WHERE id = {id}"));
    }

    [Fact]
    public void A_file_from_a_newer_build_keeps_its_version()
    {
        var path = Path.Combine(_dir, "newer.db");
        _ = new LocalStore(path);
        Exec(path, "PRAGMA user_version = 99;");
        _ = new LocalStore(path);
        Assert.Equal(99L, Scalar(path, "PRAGMA user_version"));
    }
}

/// <summary>
/// M22: a damaged store.db is set aside and replaced instead of failing every start; a store about to be upgraded is
/// copied first; two app instances opening one old file migrate it once.
/// </summary>
public class StoreRecoveryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qstore-rec-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    static object? Scalar(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    static void Exec(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    string OldFile(string name, int version = 3)
    {
        var path = Path.Combine(_dir, name);
        _ = new LocalStore(path);
        new ProfileStore(new LocalStore(path)).Save(new Profile { Id = "p1", Name = "kept", Engine = Engine.Pg, Host = "h" });
        // As the 0.1.x builds left it: no location columns, an older version.
        if (version < 4) Exec(path, $"ALTER TABLE profile DROP COLUMN location; ALTER TABLE profile DROP COLUMN file_grant_id; PRAGMA user_version = {version};");
        SqliteConnection.ClearAllPools();
        return path;
    }

    // The open store holds its WAL; Windows refuses File.ReadAllText on it unless the read shares write access.
    static string ReadShared(string file)
    {
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_set_aside_with_its_wal_and_a_fresh_store_opens()
    {
        var path = Path.Combine(_dir, "store.db");
        File.WriteAllText(path, "this is not a SQLite file, but it is long enough to have a header of its own " + new string('x', 200));
        File.WriteAllText(path + "-wal", "stale wal");
        var store = LocalStore.OpenOrRecover(path, null, out var setAside);
        Assert.NotNull(setAside);
        Assert.StartsWith("store.db.corrupt-", Path.GetFileName(setAside));
        Assert.StartsWith("this is not a SQLite file", File.ReadAllText(setAside!));
        // The old WAL never meets the new file: moved with it, or (not a WAL SQLite can read) dropped by SQLite itself.
        Assert.False(File.Exists(path + "-wal") && ReadShared(path + "-wal") == "stale wal");
        Assert.Equal((long)LocalStore.Version, Scalar(path, "PRAGMA user_version"));
        Assert.Empty(new ProfileStore(store).List());
        if (!OperatingSystem.IsWindows()) Assert.Equal(Querithm.Core.Store.PrivateFiles.FileMode, File.GetUnixFileMode(setAside!) & (UnixFileMode)0x1FF);
    }

    [Fact]
    public void A_store_whose_schema_page_is_damaged_is_set_aside()
    {
        var path = Path.Combine(_dir, "store.db");
        _ = new LocalStore(path);
        SqliteConnection.ClearAllPools();
        Exec(path, "PRAGMA wal_checkpoint(TRUNCATE);");
        // Keep the 100-byte header, overwrite the rest of page 1 (sqlite_schema) with junk.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(100, SeekOrigin.Begin);
            fs.Write(Enumerable.Repeat((byte)0xA5, 3000).ToArray());
        }
        _ = LocalStore.OpenOrRecover(path, null, out var setAside);
        Assert.NotNull(setAside);
        Assert.Equal((long)LocalStore.Version, Scalar(path, "PRAGMA user_version"));
    }

    [Fact]
    public void A_good_store_is_never_set_aside()
    {
        var path = OldFile("good.db", LocalStore.Version);
        _ = LocalStore.OpenOrRecover(path, null, out var setAside);
        Assert.Null(setAside);
        Assert.Equal("kept", Scalar(path, "SELECT name FROM profile WHERE id = 'p1'"));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void A_read_only_store_is_reported_not_set_aside()
    {
        if (OperatingSystem.IsWindows()) return;
        // An old version: the upgrade must write, which a read-only file refuses (SQLITE_READONLY) — no damage.
        var path = OldFile("readonly.db");
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        try
        {
            if (Environment.UserName == "root") return; // root writes anyway
            Assert.ThrowsAny<SqliteException>(() => LocalStore.OpenOrRecover(path, null, out _));
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
        }
        finally { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public void An_upgrade_first_copies_the_file_as_it_was_and_keeps_the_newest_two_copies()
    {
        var path = OldFile("store.db");
        for (var v = 0; v < 3; v++)
        {
            File.WriteAllText($"{path}.v9{v}.bak", "older copy");
            File.SetLastWriteTimeUtc($"{path}.v9{v}.bak", DateTime.UtcNow.AddDays(-10 + v));
        }
        _ = new LocalStore(path);
        var bak = path + ".v3.bak";
        Assert.True(File.Exists(bak));
        // The copy is the file before the upgrade: version 3, no location column, the profile row.
        Assert.Equal(3L, Scalar(bak, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(bak, "SELECT count(*) FROM pragma_table_info('profile') WHERE name = 'location'"));
        Assert.Equal("kept", Scalar(bak, "SELECT name FROM profile WHERE id = 'p1'"));
        Assert.Equal((long)LocalStore.Version, Scalar(path, "PRAGMA user_version"));
        Assert.Equal(new[] { "store.db.v3.bak", "store.db.v92.bak" }, Directory.GetFiles(_dir, "store.db.v*.bak").Select(Path.GetFileName).Order().ToArray());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        if (!OperatingSystem.IsWindows()) Assert.Equal(Querithm.Core.Store.PrivateFiles.FileMode, File.GetUnixFileMode(bak) & (UnixFileMode)0x1FF);
        // A store at the current version is not copied again.
        File.Delete(bak);
        _ = new LocalStore(path);
        Assert.False(File.Exists(bak));
    }

    [Fact]
    public void The_copy_includes_rows_still_in_the_wal()
    {
        var path = OldFile("wal.db");
        // A pooled connection keeps the WAL from being checkpointed into the main file while the row is written.
        using var keep = new SqliteConnection($"Data Source={path}");
        keep.Open();
        using (var cmd = keep.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_autocheckpoint = 0; INSERT INTO setting (key, value_json) VALUES ('in-wal', '1');";
            cmd.ExecuteNonQuery();
        }
        Assert.True(new FileInfo(path + "-wal").Length > 0);
        _ = new LocalStore(path);
        Assert.Equal("1", Scalar(path + ".v3.bak", "SELECT value_json FROM setting WHERE key = 'in-wal'"));
    }

    [Fact]
    public async Task Two_instances_opening_one_old_file_at_once_both_succeed_and_migrate_it_once()
    {
        var path = OldFile("shared.db");
        var steps = 0;
        LocalStore.AfterMigrationStep = (p, to) =>
        {
            if (p != path || to != LocalStore.Version) return;
            Interlocked.Increment(ref steps);
            // Hold the write lock a while so the other opener is surely waiting on it.
            Thread.Sleep(300);
        };
        try
        {
            using var go = new Barrier(2);
            var opens = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                go.SignalAndWait();
                return new LocalStore(path);
            })).ToArray();
            await Task.WhenAll(opens).WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally { LocalStore.AfterMigrationStep = null; }
        Assert.Equal(1, steps);
        Assert.Equal((long)LocalStore.Version, Scalar(path, "PRAGMA user_version"));
        Assert.Single(Directory.GetFiles(_dir, "shared.db.v*.bak"));
        Assert.Equal("kept", Scalar(path, "SELECT name FROM profile WHERE id = 'p1'"));
    }
    // ------------------------------------------------------------------ 8a review: downgrades and locks are not damage

    /// <summary>A current file whose schema holds a table this build's SQLite cannot parse (as a newer build might write).</summary>
    string UnparsableFile(string name, long version)
    {
        var path = OldFile(name, LocalStore.Version);
        Exec(path, $"""
            CREATE TABLE future (a INT);
            PRAGMA writable_schema = ON;
            UPDATE sqlite_master SET sql = 'CREATE TABLE future (a INT) FUTURE_OPTION' WHERE name = 'future';
            PRAGMA writable_schema = OFF;
            PRAGMA user_version = {version};
            """);
        SqliteConnection.ClearAllPools();
        return path;
    }

    [Fact]
    public void A_store_from_a_newer_build_that_this_build_cannot_read_is_refused_and_left_as_it_is()
    {
        var path = UnparsableFile("newer.db", LocalStore.Version + 1);
        var before = File.ReadAllBytes(path);

        var ex = Assert.Throws<StoreRefusedException>(() => LocalStore.OpenOrRecover(path, null, out _));

        Assert.Contains("written by a newer version of Querithm", ex.Message);
        Assert.Contains($"data version {LocalStore.Version + 1}", ex.Message);
        SqliteConnection.ClearAllPools();
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*")); // never quarantined: the newer build reads it again
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_store_whose_schema_does_not_parse_is_refused_not_set_aside()
    {
        var path = UnparsableFile("schema.db", LocalStore.Version);
        var ex = Assert.Throws<StoreRefusedException>(() => LocalStore.OpenOrRecover(path, null, out _));
        Assert.Contains("schema this version cannot read", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_store_another_connection_holds_exclusively_is_reported_and_never_set_aside()
    {
        var path = OldFile("locked.db", LocalStore.Version);
        using (var holder = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            holder.Open();
            using (var cmd = holder.CreateCommand())
            {
                cmd.CommandText = "PRAGMA locking_mode = EXCLUSIVE; BEGIN EXCLUSIVE; UPDATE profile SET name = name;";
                cmd.ExecuteNonQuery();
            }
            var ex = Assert.ThrowsAny<SqliteException>(() => LocalStore.OpenOrRecover(path, null, out _, null, TimeSpan.FromSeconds(1)));
            Assert.Contains(ex.SqliteErrorCode, new[] { 5, 6 }); // BUSY / LOCKED: says nothing about the file
            Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
        }
        SqliteConnection.ClearAllPools();
        // Once the other connection lets go, the same file opens with its data.
        _ = LocalStore.OpenOrRecover(path, null, out var setAside);
        Assert.Null(setAside);
        Assert.Equal("kept", Scalar(path, "SELECT name FROM profile WHERE id = 'p1'"));
    }

    // ------------------------------------------------------------------ 8a review: history secrets an older build kept

    [Fact]
    public void The_v5_step_masks_secrets_in_history_an_older_build_kept_while_the_copy_before_it_keeps_them()
    {
        var path = OldFile("hist.db", 4);
        Exec(path, """
            INSERT INTO profile (id, name, engine) VALUES ('pg1', 'pg', 'pg');
            INSERT INTO history (profile_id, executed_at, sql, status, error) VALUES ('pg1', 1, 'ALTER ROLE app PASSWORD ''Hunter2!''', 'Failed', 'syntax error at or near ''Hunter2!''');
            INSERT INTO history (profile_id, executed_at, sql, status) VALUES ('pg1', 2, 'ALTER ROLE app PASSWORD ''***''', 'Done');
            INSERT INTO history (profile_id, executed_at, sql, status) VALUES ('gone', 3, 'CREATE USER u IDENTIFIED BY ''zqmysql9''', 'Done');
            INSERT INTO history (profile_id, executed_at, sql, status) VALUES ('pg1', 4, 'SELECT 1', 'Done');
            ALTER TABLE history DROP COLUMN preview;
            ALTER TABLE history DROP COLUMN sql_length;
            PRAGMA user_version = 4;
            """);
        SqliteConnection.ClearAllPools();
        var redact = LocalStore.HistoryRedactor([new Querithm.Providers.SqlServer.SqlServerProvider(), new Querithm.Providers.Postgres.PostgresProvider(), new Querithm.Providers.MySql.MySqlProvider()]);

        var history = new HistoryStore(new LocalStore(path, null, redact));

        var rows = history.List(new HistoryListParams { Limit = 10 }).OrderBy(r => r.ExecutedAt).ToArray();
        Assert.Equal(
            ["ALTER ROLE app PASSWORD <redacted>", "ALTER ROLE app PASSWORD <redacted>", "CREATE USER u IDENTIFIED BY <redacted>", "SELECT 1"],
            rows.Select(r => r.Sql));
        Assert.DoesNotContain("Hunter2!", rows[0].Error);
        // The full-text index no longer holds the secrets as terms.
        Assert.Empty(history.Search(new HistorySearchParams { Text = "Hunter2" }));
        Assert.Empty(history.Search(new HistorySearchParams { Text = "zqmysql9" }));
        Assert.Single(history.Search(new HistorySearchParams { Text = "IDENTIFIED" }));
        Assert.Equal(0L, Scalar(path, "SELECT count(*) FROM history WHERE sql LIKE '%Hunter2%' OR error LIKE '%Hunter2%' OR sql LIKE '%***%'"));
        // The copy made before the upgrade is the file as it was: it still holds them (documented, §8).
        Assert.Equal(1L, Scalar(path + ".v4.bak", "SELECT count(*) FROM history WHERE sql LIKE '%Hunter2!%'"));
    }
}


/// <summary>Triggers in the metadata cache (§8): parent ids, definition fill, and incremental refresh.</summary>
public class TriggerMetadataStoreTests
{
    [Fact]
    public void Triggers_keep_their_parent_and_join_the_definition_fill()
    {
        var store = new MetadataStore(LocalStore.InMemory());
        store.ApplyRefresh("p", "db", true,
            [
                new ObjectStamp(1, "dbo", "Orders", ObjectKind.Table, 1, null),
                new ObjectStamp(2, "dbo", "trg_Orders_audit", ObjectKind.Trigger, 1, null, 1),
                new ObjectStamp(3, "dbo", "v", ObjectKind.View, 1, null),
            ], [], new MetadataBundle([], [], [], []), "16");
        var snap = store.Load("p", "db");
        var trigger = snap.Objects.Single(o => o.Kind == ObjectKind.Trigger);
        Assert.Equal((1L, (SnapColumn[]?)null), (trigger.ParentObjectId, trigger.Columns));
        Assert.All(snap.Objects.Where(o => o.Kind != ObjectKind.Trigger), o => Assert.Null(o.ParentObjectId));
        Assert.Equal([2L, 3L], store.MissingDefinitionIds("p", "db").Order());
        // A login without VIEW DEFINITION reads NULL: recorded as "not available", not fetched again.
        store.CacheDefinitions("p", "db", [(2, null), (3, "CREATE VIEW dbo.v AS SELECT 1 AS x")]);
        Assert.Empty(store.MissingDefinitionIds("p", "db"));
        Assert.Null(store.Definitions("p", "db")[2]);
    }

    [Fact]
    public void An_incremental_refresh_replaces_a_changed_trigger_and_drops_a_removed_one()
    {
        var store = new MetadataStore(LocalStore.InMemory());
        store.ApplyRefresh("p", "db", true,
            [new ObjectStamp(1, "public", "items", ObjectKind.Table, 1, null), new ObjectStamp(2, "public", "items", ObjectKind.Trigger, 1, null, 1),
             new ObjectStamp(3, "public", "t_gone", ObjectKind.Trigger, 1, null, 1)], [], new MetadataBundle([], [], [], []), "16");
        store.CacheDefinitions("p", "db", [(2, "CREATE TRIGGER items BEFORE UPDATE ON public.items FOR EACH ROW EXECUTE FUNCTION f();")]);
        store.ApplyRefresh("p", "db", false, [new ObjectStamp(2, "public", "items", ObjectKind.Trigger, 9, null, 1)], [3], new MetadataBundle([], [], [], []), "16");
        var snap = store.Load("p", "db");
        // PostgreSQL lets a trigger share its table's name: both stay, told apart by kind.
        Assert.Equal([(ObjectKind.Table, 1L), (ObjectKind.Trigger, 2L)], snap.Objects.Select(o => (o.Kind, o.Id)).OrderBy(x => x.Id));
        Assert.Equal(9L, store.GetMarkers("p", "db")[2]);
        // The changed trigger's cached text is dropped, so the next fill reads the new definition.
        Assert.Equal([2L], store.MissingDefinitionIds("p", "db"));
    }
}
