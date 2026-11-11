using System.Data.Common;
using System.Runtime.CompilerServices;
using Oracle.ManagedDataAccess.Client;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>
/// The current schema of each connection as far as Querithm knows it (D4 S10): an unqualified table name in a query
/// resolves there, so the grid's result origins need it. Read at open; a batch that may change it — ALTER SESSION, CALL,
/// a PL/SQL block (EXECUTE IMMEDIATE, DBMS_SQL) or a client command — makes it unknown until it is read again after that
/// batch (one round trip, only after such a batch). While unknown, a result naming its table without a schema stays
/// read-only: a guessed schema could edit another table of the same name.
/// </summary>
internal static class OracleSessionSchema
{
    static readonly ConditionalWeakTable<DbConnection, StrongBox<string?>> Current = new();

    internal const string Sql = "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL";

    /// <summary>The schema, or null when it is unknown (never read, or a batch since may have changed it).</summary>
    public static string? Of(DbConnection connection) => Current.TryGetValue(connection, out var box) ? box.Value : null;

    public static void Set(DbConnection connection, string? schema) => Current.AddOrUpdate(connection, new StrongBox<string?>(string.IsNullOrEmpty(schema) ? null : schema));

    /// <summary>Before a batch runs (PrepareCommand): one that may change the schema makes it unknown.</summary>
    public static void BeforeBatch(DbConnection connection, string batchText)
    {
        if (MayChange(batchText)) Set(connection, null);
    }

    /// <summary>Plain SQL other than ALTER SESSION and CALL leaves the current schema as it was.</summary>
    internal static bool MayChange(string batchText)
    {
        foreach (var st in OracleSplitter.Split(batchText))
        {
            var code = st.Code.Take(2).ToList();
            if (code.Count == 0) continue;
            if (st.Kind != OracleStatementKind.Sql) return true;
            if (code[0].Is("CALL") || code[0].Is("ALTER") && code.Count > 1 && code[1].Is("SESSION")) return true;
        }
        return false;
    }

    /// <summary>After a batch: reads the schema again when it is unknown. A failure leaves it unknown (the next batch tries again).</summary>
    public static async Task RefreshAsync(DbConnection connection, CancellationToken ct)
    {
        if (Of(connection) != null || connection is not OracleConnection { State: System.Data.ConnectionState.Open }) return;
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = Sql;
            Set(connection, await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string);
        }
        catch (OracleException) { /* stays unknown */ }
    }
}
