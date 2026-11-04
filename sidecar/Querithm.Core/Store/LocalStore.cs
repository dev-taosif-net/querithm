using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Store;

/// <summary>
/// 8a review: store.db cannot be opened by this build and was left exactly as it is — it was written by a newer build
/// (a downgrade whose schema this SQLite cannot read), or its schema does not parse. Never set aside as damaged: the
/// newer build reads it again. The host then runs on an empty in-memory store and tells the user (system.notices).
/// </summary>
public sealed class StoreRefusedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The one SQLite file (§8). Pooled connections, WAL, schema applied on open.</summary>
public sealed class LocalStore
{
    readonly string _connectionString;

    public string Path { get; }

    /// <param name="redactHistory">The v5 step masks secrets in history rows an older build kept (<see cref="HistoryRedactor"/>); null skips it.</param>
    /// <param name="busyTimeout">How long an open waits for a lock another connection holds (default 30 s; shorter in tests).</param>
    public LocalStore(string path, ILogger? log = null, Func<string?, string, Redaction>? redactHistory = null, TimeSpan? busyTimeout = null)
    {
        Path = path;
        _log = log;
        _redactHistory = redactHistory;
        _busy = busyTimeout ?? DefaultBusyTimeout;
        var dir = System.IO.Path.GetDirectoryName(path);
        // L4: the folder and the file are the user's alone (Unix 0700 / 0600) before SQLite writes anything into them.
        if (!string.IsNullOrEmpty(dir)) PrivateFiles.Folder(dir);
        PrivateFiles.File(path);
        _connectionString = ConnectionString(path, pooling: true, _busy);
        Initialize();
        // SQLite gives new -wal / -shm files the main file's mode; ones an older build left wider are narrowed here.
        PrivateFiles.NarrowIfExists(path + "-wal");
        PrivateFiles.NarrowIfExists(path + "-shm");
    }

    static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(30);
    readonly TimeSpan _busy = DefaultBusyTimeout;
    readonly Func<string?, string, Redaction>? _redactHistory;

    static string ConnectionString(string path, bool pooling, TimeSpan? busy = null) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = pooling,
        DefaultTimeout = (int)Math.Max(1, (busy ?? DefaultBusyTimeout).TotalSeconds),
    }.ToString();

    /// <summary>
    /// M22: opens the store at start. A file SQLite reports damaged (SQLITE_CORRUPT / SQLITE_NOTADB, or — when opening or
    /// migrating fails some other way that is not a busy, locked, full, read-only or I/O condition — a failing
    /// <c>PRAGMA quick_check</c>) is set aside with its -wal / -shm as <c>store.db.corrupt-&lt;UTC time&gt;</c> and a fresh
    /// store is made; <paramref name="setAsideAs"/> then names the file the damaged one was moved to. quick_check is not run
    /// on a good open: it reads every page (measured 52 ms for a 29 MB store with a warm cache; more from a cold disk), which the
    /// 1.5 s cold start does not have room for (§14). Any other failure is thrown as before.
    /// </summary>
    /// <remarks>
    /// 8a review: a failure on a file whose <c>user_version</c> is newer than <see cref="Version"/> (a downgrade), or a schema
    /// that does not parse ("malformed database schema" is SQLITE_CORRUPT too), is never damage to set aside: it throws
    /// <see cref="StoreRefusedException"/> with the reason and leaves the file as it is. Busy, locked, read-only and I/O
    /// failures are thrown as before (never set aside).
    /// </remarks>
    public static LocalStore OpenOrRecover(string path, ILogger? log, out string? setAsideAs, Func<string?, string, Redaction>? redactHistory = null) =>
        OpenOrRecover(path, log, out setAsideAs, redactHistory, null);

    internal static LocalStore OpenOrRecover(string path, ILogger? log, out string? setAsideAs, Func<string?, string, Redaction>? redactHistory, TimeSpan? busyTimeout)
    {
        setAsideAs = null;
        try
        {
            return new LocalStore(path, log, redactHistory, busyTimeout);
        }
        catch (SqliteException ex) when (Refusal(ex, path) is { } why)
        {
            SqliteConnection.ClearPool(new SqliteConnection(ConnectionString(path, pooling: true, busyTimeout)));
            log?.LogError("The local store was not opened ({Code}: {Message}): {Reason}", ex.SqliteErrorCode, ex.Message, why);
            throw new StoreRefusedException(why, ex);
        }
        catch (SqliteException ex) when (IsDamaged(ex, path))
        {
            // The failed open's pooled connections hold the file (Windows cannot rename it while they do).
            SqliteConnection.ClearPool(new SqliteConnection(ConnectionString(path, pooling: true, busyTimeout)));
            setAsideAs = SetAside(path, DateTime.UtcNow);
            log?.LogError("The local store could not be read ({Code}: {Message}); it was set aside as {File} and a new one was made",
                ex.SqliteErrorCode, ex.Message, System.IO.Path.GetFileName(setAsideAs));
            return new LocalStore(path, log, redactHistory, busyTimeout);
        }
    }

    /// <summary>Why a failed open must leave the file alone: written by a newer build, or a schema this SQLite cannot parse; null otherwise.</summary>
    static string? Refusal(SqliteException ex, string path)
    {
        if (ex.SqliteErrorCode is not (SqliteCorrupt or 1 /* ERROR */)) return null;
        if (FileUserVersion(path) is { } v && v > Version)
            return $"Querithm's local data file (store.db) was written by a newer version of Querithm (data version {v}; this one reads up to {Version}) and cannot be read here. It was left as it is: update Querithm to use it again.";
        if (ex.SqliteErrorCode == SqliteCorrupt && ex.Message.Contains("malformed database schema", StringComparison.OrdinalIgnoreCase))
            return "Querithm's local data file (store.db) has a schema this version cannot read (written by a newer Querithm, or damaged). It was left as it is: update Querithm, or move store.db aside to start with an empty one.";
        return null;
    }

    /// <summary>
    /// <c>user_version</c> from the file's header (bytes 60–63, big-endian) — readable when its schema is not; the WAL may
    /// hold a newer page 1, so SQLite's own answer is asked first.
    /// </summary>
    static long? FileUserVersion(string path)
    {
        try
        {
            using var c = new SqliteConnection(ConnectionString(path, pooling: false, TimeSpan.FromSeconds(1)));
            c.Open();
            using var cmd = c.Cmd("PRAGMA user_version;");
            return (long)cmd.ExecuteScalar()!;
        }
        catch (SqliteException) { /* the header below */ }
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[100];
            if (f.Read(header, 0, header.Length) < header.Length || !header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8)) return null;
            return BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(60, 4));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    const int SqliteCorrupt = 11, SqliteNotADb = 26;

    /// <summary>Conditions that say nothing about the file's content: never a reason to set the user's data aside.</summary>
    static readonly int[] NotDamage = [3 /* PERM */, 5 /* BUSY */, 6 /* LOCKED */, 7 /* NOMEM */, 8 /* READONLY */, 10 /* IOERR */, 13 /* FULL */, 14 /* CANTOPEN */];

    static bool IsDamaged(SqliteException ex, string path)
    {
        if (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADb) return true;
        if (NotDamage.Contains(ex.SqliteErrorCode)) return false;
        try
        {
            using var c = new SqliteConnection(ConnectionString(path, pooling: false));
            c.Open();
            using var cmd = c.Cmd("PRAGMA quick_check(1);");
            return cmd.ExecuteScalar() as string != "ok";
        }
        catch (SqliteException check) { return check.SqliteErrorCode is SqliteCorrupt or SqliteNotADb; }
    }

    /// <summary>Moves the store and its -wal / -shm / -journal aside (a leftover WAL would be replayed into the new file).</summary>
    internal static string SetAside(string path, DateTime utc)
    {
        var target = $"{path}.corrupt-{utc:yyyyMMdd'T'HHmmss'Z'}";
        for (var n = 2; System.IO.File.Exists(target); n++) target = $"{path}.corrupt-{utc:yyyyMMdd'T'HHmmss'Z'}-{n}";
        System.IO.File.Move(path, target);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (System.IO.File.Exists(path + suffix)) System.IO.File.Move(path + suffix, target + suffix);
        return target;
    }

    /// <summary>In-memory store for tests (shared cache keeps the db alive while any connection is open).</summary>
    public static LocalStore InMemory() => new($"querithm-{Guid.NewGuid():N}", inMemory: true);

    readonly SqliteConnection? _keepAlive;

    LocalStore(string uri, bool inMemory)
    {
        Path = uri;
        _inMemory = inMemory;
        _connectionString = $"Data Source={uri};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        Initialize();
    }

    /// <summary>The schema version Schema.sql describes (PRAGMA user_version).</summary>
    public const int Version = 6;

    /// <summary>
    /// Steps from version N-1 to N for files written by older builds, run before Schema.sql so its CREATE … IF NOT
    /// EXISTS statements meet the current tables. A fresh file skips them: Schema.sql already is the newest schema.
    /// Each step must tolerate a file that already has its change: builds up to 0.1.6 reset user_version to 1 on
    /// every open, so a newer file opened once by an old build comes back at version 1 with the newer columns.
    /// Downgrade rule (§8): an older build keeps working on a newer file only while every step is additive (new tables,
    /// new nullable columns, data it may ignore); a step that renames, drops or changes the meaning of what an older build
    /// reads needs a new file instead.
    /// </summary>
    static readonly (int To, Action<LocalStore, SqliteConnection, SqliteTransaction> Apply)[] Migrations =
    [
        // Triggers join the snapshot: existing snapshots have none, so the next refresh must look again.
        (2, (_, c, tx) =>
        {
            if (!HasColumn(c, tx, "obj", "parent_object_id"))
                c.Cmd("ALTER TABLE obj ADD COLUMN parent_object_id INTEGER;", tx).ExecuteNonQuery();
            c.Cmd("UPDATE schema_snapshot SET status = 'stale';", tx).ExecuteNonQuery();
        }),
        // The snippet table: created by Schema.sql's CREATE TABLE IF NOT EXISTS in the same transaction.
        (3, (_, _, _) => { }),
        // Phase D0a: where a profile's database lives. NULL = a server, which every existing profile is; host and port keep
        // their encodings untouched (socket directories, failover lists, tcp:/np:/lpc:, host\instance).
        (4, (_, c, tx) =>
        {
            // A file without the table gets it from Schema.sql, columns included.
            if (!HasTable(c, tx, "profile")) return;
            if (!HasColumn(c, tx, "profile", "location")) c.Cmd("ALTER TABLE profile ADD COLUMN location TEXT;", tx).ExecuteNonQuery();
            if (!HasColumn(c, tx, "profile", "file_grant_id")) c.Cmd("ALTER TABLE profile ADD COLUMN file_grant_id TEXT;", tx).ExecuteNonQuery();
        }),
        // L17: history previews. Additive: an older build ignores both columns and writes rows without them, which list
        // and search read with substr / length instead. No backfill (it would read every text at start); retention ages
        // the old rows out. 8a review: the same step masks the secrets of rows an older build kept (RedactHistory).
        (5, (self, c, tx) =>
        {
            if (!HasTable(c, tx, "history")) return;
            if (!HasColumn(c, tx, "history", "preview")) c.Cmd("ALTER TABLE history ADD COLUMN preview TEXT;", tx).ExecuteNonQuery();
            if (!HasColumn(c, tx, "history", "sql_length")) c.Cmd("ALTER TABLE history ADD COLUMN sql_length INTEGER;", tx).ExecuteNonQuery();
            if (self._redactHistory is { } redact) self.RedactHistory(c, tx, redact);
        }),
        // Grid-edit audit: a MySQL / MariaDB table whose engine cannot roll back (MyISAM, MEMORY …) is read-only in the grid, and
        // a partial / filtered / expression / disabled unique index is never a row key. Additive: an older build ignores both
        // columns. Rows cached before them carry neither flag and an incremental refresh keeps unchanged objects, so every
        // snapshot is marked empty: its next refresh is a full one (§8) that reads both flags. The cached rows stay shown.
        (6, (_, c, tx) =>
        {
            if (HasTable(c, tx, "obj") && !HasColumn(c, tx, "obj", "no_rollback_engine"))
                c.Cmd("ALTER TABLE obj ADD COLUMN no_rollback_engine TEXT;", tx).ExecuteNonQuery();
            if (HasTable(c, tx, "idx") && !HasColumn(c, tx, "idx", "not_key"))
                c.Cmd("ALTER TABLE idx ADD COLUMN not_key INTEGER;", tx).ExecuteNonQuery();
            if (HasTable(c, tx, "schema_snapshot")) c.Cmd("UPDATE schema_snapshot SET status = 'empty';", tx).ExecuteNonQuery();
        }),
    ];

    static bool HasTable(SqliteConnection c, SqliteTransaction tx, string table)
    {
        using var cmd = c.Cmd("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $t;", tx, ("$t", table));
        return (long)cmd.ExecuteScalar()! > 0;
    }

    static bool HasColumn(SqliteConnection c, SqliteTransaction tx, string table, string column)
    {
        using var cmd = c.Cmd("SELECT count(*) FROM pragma_table_info($t) WHERE name = $c;", tx, ("$t", table), ("$c", column));
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// 8a review: history rows written before L4 kept secrets as typed, and L4's own <c>'***'</c> was a runnable literal;
    /// the v5 step masks both with the row's engine rules (<see cref="HistoryRedactor"/>), in the error text too, and keeps
    /// the full-text index in step (its terms held the secrets). It reads every history row once, at this upgrade only.
    /// Old content is overwritten in the file (secure_delete for the step, the index optimized), but the copy made before
    /// the upgrade (<c>store.db.v&lt;N&gt;.bak</c>, §8) keeps it as it was, until a later upgrade's copy replaces it.
    /// A failure leaves the rows as they were (a savepoint) and the upgrade goes ahead.
    /// </summary>
    void RedactHistory(SqliteConnection c, SqliteTransaction tx, Func<string?, string, Redaction> redact)
    {
        var changed = new List<(long Id, string Old, string Text, string? Error)>();
        var withEngine = HasTable(c, tx, "profile") && HasColumn(c, tx, "profile", "engine");
        var withError = HasColumn(c, tx, "history", "error");
        using (var read = c.Cmd($"""
                   SELECT h.id, h.sql, {(withError ? "h.error" : "NULL")}, {(withEngine ? "p.engine" : "NULL")}
                   FROM history h {(withEngine ? "LEFT JOIN profile p ON p.id = h.profile_id" : "")} WHERE h.sql IS NOT NULL;
                   """, tx))
        using (var r = read.ExecuteReader())
            while (r.Read())
            {
                var sql = r.GetString(1);
                var masked = redact(r.IsDBNull(3) ? null : r.GetString(3), sql);
                if (masked.Text == sql) continue;
                changed.Add((r.GetInt64(0), sql, masked.Text, masked.Mask(r.IsDBNull(2) ? null : r.GetString(2))));
            }
        if (changed.Count == 0) return;
        var fts = HasTable(c, tx, "history_fts");
        long secure;
        using (var get = c.Cmd("PRAGMA secure_delete;", tx)) secure = (long)get.ExecuteScalar()!;
        tx.Save("redact_history");
        try
        {
            c.Cmd("PRAGMA secure_delete = ON;", tx).ExecuteNonQuery();
            foreach (var (id, old, text, error) in changed)
            {
                if (fts) c.Cmd("INSERT INTO history_fts(history_fts, rowid, sql) VALUES ('delete', $id, $old);", tx, ("$id", id), ("$old", old)).ExecuteNonQuery();
                c.Cmd("UPDATE history SET sql = $sql, preview = NULL, sql_length = NULL" + (withError ? ", error = $err" : "") + " WHERE id = $id;", tx,
                    ("$id", id), ("$sql", text), ("$err", error)).ExecuteNonQuery();
                if (fts) c.Cmd("INSERT INTO history_fts(rowid, sql) VALUES ($id, $sql);", tx, ("$id", id), ("$sql", text)).ExecuteNonQuery();
            }
            // Deleted index entries stay in its segments until they merge: one merge now drops the secrets' terms.
            if (fts) c.Cmd("INSERT INTO history_fts(history_fts) VALUES ('optimize');", tx).ExecuteNonQuery();
            tx.Release("redact_history");
            _log?.LogInformation("Masked secrets in {Count} history entries an older build kept", changed.Count);
        }
        catch (SqliteException ex)
        {
            tx.Rollback("redact_history");
            tx.Release("redact_history");
            _log?.LogWarning("Masking secrets in the history an older build kept failed ({Message}); the entries stay as they were", ex.Message);
        }
        finally { c.Cmd($"PRAGMA secure_delete = {(secure != 0 ? "ON" : "OFF")};", tx).ExecuteNonQuery(); }
    }

    /// <summary>
    /// The redactor for <see cref="RedactHistory"/>: the row's engine's rules (its profile's engine), every engine's in turn
    /// when the profile is gone or its engine is not in this build.
    /// </summary>
    public static Func<string?, string, Redaction> HistoryRedactor(IReadOnlyList<IDbProvider> providers) => (engine, text) =>
    {
        if (engine != null && EnumText.TryParse<Engine>(engine, out var e) && providers.FirstOrDefault(p => p.Engine == e) is { } one)
            return one.Language.RedactSecrets(text);
        var secrets = new List<string>();
        foreach (var p in providers)
        {
            var r = p.Language.RedactSecrets(text);
            text = r.Text;
            secrets.AddRange(r.Secrets);
        }
        return new Redaction(text, secrets.Distinct().OrderByDescending(x => x.Length).ToArray());
    };

    /// <summary>Test hook: (path, version) after each migration step inside the transaction; throw to simulate a crash mid-migration.</summary>
    internal static Action<string, int>? AfterMigrationStep;

    /// <summary>How many <c>store.db.v&lt;N&gt;.bak</c> copies are kept (the newest).</summary>
    internal const int KeptBackups = 2;

    readonly bool _inMemory;
    readonly ILogger? _log;

    /// <summary>
    /// M22: before the first migration step, a copy of the file as it is — WAL content included, since VACUUM INTO reads
    /// through SQLite — as <c>store.db.v&lt;from&gt;.bak</c> (0600 on Unix), the newest <see cref="KeptBackups"/> kept. It runs
    /// on a second connection while the migrating one holds the write lock without having written, so it copies the
    /// file exactly as the older build left it. A copy that cannot be made (a full disk) is reported and the migration
    /// goes ahead: the steps are one transaction either way.
    /// </summary>
    void Backup(long from)
    {
        var bak = $"{Path}.v{from}.bak";
        var tmp = bak + ".tmp";
        try
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
            // An empty file is a valid VACUUM INTO target; made first so the copy is the user's alone from the start.
            PrivateFiles.File(tmp);
            using (var c = new SqliteConnection(ConnectionString(Path, pooling: false)))
            {
                c.Open();
                using var cmd = c.Cmd("VACUUM INTO $file;", null, ("$file", tmp));
                cmd.ExecuteNonQuery();
            }
            System.IO.File.Move(tmp, bak, overwrite: true);
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
            foreach (var old in Directory.GetFiles(dir, System.IO.Path.GetFileName(Path) + ".v*.bak")
                         .OrderByDescending(System.IO.File.GetLastWriteTimeUtc).Skip(KeptBackups))
                System.IO.File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch (IOException) { /* best effort */ }
            _log?.LogWarning("The copy of the local store before its upgrade from version {From} could not be made ({Message}); upgrading without one", from, ex.Message);
        }
    }

    void Initialize()
    {
        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
        using var schema = Assembly.GetExecutingAssembly().GetManifestResourceStream("Querithm.Core.Store.Schema.sql")
                           ?? throw new InvalidOperationException("Schema.sql resource missing");
        using var reader = new StreamReader(schema);
        // One transaction for every step: a crash mid-migration leaves the file at its old version, and the next
        // open runs the same steps again (SQLite DDL is transactional).
        // BEGIN IMMEDIATE: the version is read under the write lock, so a second app instance opening the same file waits
        // here (busy timeout, then Microsoft.Data.Sqlite's retry up to the command timeout) and then sees the new version
        // instead of migrating again.
        using var tx = c.BeginTransaction(deferred: false);
        long current;
        using (var cmd = c.Cmd("PRAGMA user_version;", tx)) current = (long)cmd.ExecuteScalar()!;
        if (current >= 1 && current < Version && !_inMemory) Backup(current);
        if (current >= 1)
            foreach (var (to, apply) in Migrations.Where(m => m.To > current))
            {
                apply(this, c, tx);
                AfterMigrationStep?.Invoke(Path, to);
            }
        c.Cmd(reader.ReadToEnd(), tx).ExecuteNonQuery();
        // A file from a newer build keeps its version.
        if (current < Version) c.Cmd($"PRAGMA user_version = {Version};", tx).ExecuteNonQuery();
        tx.Commit();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout = {(int)Math.Min(5000, _busy.TotalMilliseconds)}; PRAGMA synchronous = NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }
}

static class SqliteExtensions
{
    public static SqliteCommand Cmd(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? Long(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static int? Int(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static bool Bool(this SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetInt64(i) != 0;
}
