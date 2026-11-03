using System.Data.Common;

namespace Querithm.Contract.Providers;

/// <summary>
/// Generic loader for engines without a native bulk path (MySQL-ready): multi-row INSERT statements with
/// typed parameters from the engine's <see cref="IDataEditDialect"/>. A value the dialect cannot convert rejects its row.
/// Its own transaction (<see cref="BulkTxMode.Own"/>) goes through the provider's <see cref="ITransactionControl"/>.
/// </summary>
public sealed class BatchInsertLoader(IDataEditDialect dialect, ITransactionControl transactions) : IBulkLoader
{
    const int MaxParams = 2000;

    public async Task LoadAsync(DbConnection connection, BulkTarget target, IBulkRows rows, ImportRunOptions options, BulkTransaction tx, int? commandTimeoutS, CancellationToken ct)
    {
        if (tx.Mode != BulkTxMode.Own)
        {
            await InsertAsync(connection, target, rows, tx.Mode == BulkTxMode.Batches, commandTimeoutS, ct).ConfigureAwait(false);
            return;
        }
        await transactions.BeginAsync(connection, ct).ConfigureAwait(false);
        try
        {
            await InsertAsync(connection, target, rows, false, commandTimeoutS, ct).ConfigureAwait(false);
            await tx.CommitAsync(() => transactions.CommitAsync(connection, ct)).ConfigureAwait(false);
        }
        catch
        {
            try { using var cleanup = Cleanup.Start(); await transactions.RollbackAsync(connection, cleanup.Token).ConfigureAwait(false); } catch { /* connection state is re-read afterwards */ }
            throw;
        }
    }

    /// <summary>Each INSERT auto-commits when there is no transaction around it (batches mode reports that as committed).</summary>
    async Task InsertAsync(DbConnection connection, BulkTarget target, IBulkRows rows, bool durable, int? commandTimeoutS, CancellationToken ct)
    {
        var table = $"{dialect.Quote(target.Schema)}.{dialect.Quote(target.Table)}";
        var cols = string.Join(", ", target.Columns.Select(c => dialect.Quote(c.Name)));
        var perStatement = Math.Max(1, Math.Min(500, MaxParams / Math.Max(1, target.Columns.Count)));
        long sent = 0;
        await using var probe = connection.CreateCommand();
        var more = true;
        while (more)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = commandTimeoutS ?? 0;
            var tuples = new List<string>();
            var param = 0;
            more = false;
            while (rows.Read())
            {
                var cur = rows.Current;
                string? bad = null;
                for (var i = 0; i < target.Columns.Count && bad == null; i++)
                {
                    probe.Parameters.Clear();
                    try { dialect.AddValue(probe, 0, target.Columns[i].Type, Value(cur, i)); }
                    catch (FormatException fe) { bad = $"{target.Columns[i].Name}: {fe.Message}"; }
                }
                if (bad != null) { rows.Reject(bad); continue; }
                var parts = new List<string>();
                for (var i = 0; i < target.Columns.Count; i++)
                {
                    dialect.AddValue(cmd, param, target.Columns[i].Type, Value(cur, i));
                    parts.Add(dialect.TypedPlaceholder(param++, target.Columns[i].Type));
                }
                tuples.Add("(" + string.Join(", ", parts) + ")");
                if (tuples.Count >= perStatement) { more = true; break; }
            }
            if (tuples.Count == 0) break;
            cmd.CommandText = $"INSERT INTO {table} ({cols}) VALUES {string.Join(", ", tuples)}";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            sent += tuples.Count;
            rows.Loaded(sent, durable);
        }
    }

    static GridValue Value(IReadOnlyList<string?> row, int i) => i < row.Count && row[i] is { } v ? new GridValue { Text = v } : new GridValue { IsNull = true };
}
