using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Querithm.Providers.Sqlite;

/// <summary>
/// Every physical SQLite connection is hardened before any user SQL (Phase D1; SQLite's guidance for untrusted database
/// files): foreign keys enforced and verified, trusted_schema off, defensive mode, extension loading off, no attached
/// databases (SQLITE_LIMIT_ATTACHED = 0), temporary storage in memory (no spill files), and an authorizer that denies
/// ATTACH / DETACH, load_extension() and file-changing pragmas — the native backing of the classifier's refusals.
/// </summary>
public static class SqliteHardening
{
    /// <summary>Pragmas the authorizer refuses to set (reading them stays allowed).</summary>
    static readonly HashSet<string> RefusedPragmas = new(StringComparer.OrdinalIgnoreCase)
    {
        "temp_store_directory", "data_store_directory", "writable_schema", "trusted_schema", "journal_mode", "schema_version",
        "legacy_file_format", "cell_size_check", "mmap_size", "locking_mode",
    };

    // Kept in a static field: the native side holds a pointer to it for the connection's life.
    static readonly strdelegate_authorizer Authorizer = Authorize;

    public const int SqliteAuth = 23;

    static int Authorize(object userData, int action, string? p0, string? p1, string? dbName, string? trigger)
    {
        switch (action)
        {
            case raw.SQLITE_ATTACH:
            case raw.SQLITE_DETACH:
                return raw.SQLITE_DENY;
            case raw.SQLITE_FUNCTION when string.Equals(p1, "load_extension", StringComparison.OrdinalIgnoreCase):
                return raw.SQLITE_DENY;
            case raw.SQLITE_PRAGMA when p1 != null && p0 != null && RefusedPragmas.Contains(p0):
                return raw.SQLITE_DENY;
            default:
                return raw.SQLITE_OK;
        }
    }

    /// <summary>Applies the settings and verifies the ones that matter; throws when the build cannot enforce them.</summary>
    public static void Apply(SqliteConnection c)
    {
        var db = c.Handle ?? throw new InvalidOperationException("The SQLite connection is not open.");
        // Pragmas first (the authorizer below would refuse setting trusted_schema), and outside any transaction.
        Exec(c, "PRAGMA foreign_keys = ON; PRAGMA trusted_schema = OFF; PRAGMA temp_store = MEMORY;");
        Check(raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_DEFENSIVE, 1, out var defensive), defensive == 1, "defensive mode");
        Check(raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_TRUSTED_SCHEMA, 0, out var trusted), trusted == 0, "trusted_schema");
        Check(raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_ENABLE_LOAD_EXTENSION, 0, out var ext), ext == 0, "extension loading");
        Check(raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_WRITABLE_SCHEMA, 0, out var writable), writable == 0, "writable_schema");
        raw.sqlite3_limit(db, raw.SQLITE_LIMIT_ATTACHED, 0);
        if (raw.sqlite3_limit(db, raw.SQLITE_LIMIT_ATTACHED, -1) != 0) throw new NotSupportedException("SQLite would not limit attached databases on this build.");
        Check(raw.sqlite3_set_authorizer(db, Authorizer, null), true, "the authorizer");
        if (Scalar(c, "PRAGMA foreign_keys") != 1) throw new NotSupportedException("SQLite would not enforce foreign keys on this connection.");
        if (Scalar(c, "PRAGMA trusted_schema") != 0) throw new NotSupportedException("SQLite would not turn trusted_schema off on this connection.");
    }

    static void Check(int rc, bool ok, string what)
    {
        if (rc != raw.SQLITE_OK || !ok) throw new NotSupportedException($"This SQLite build cannot enforce {what} (code {rc}); file databases cannot be opened safely.");
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Interrupts whatever runs on the connection (thread-safe, connection-wide); SqliteCommand.Cancel is a no-op.</summary>
    public static void Interrupt(SqliteConnection c)
    {
        if (c.Handle is { } db) raw.sqlite3_interrupt(db);
    }

    /// <summary>True while a transaction is open (sqlite3_get_autocommit = 0): exact, no SQL.</summary>
    public static bool InTransaction(SqliteConnection c) => c.Handle is { } db && raw.sqlite3_get_autocommit(db) == 0;
}
