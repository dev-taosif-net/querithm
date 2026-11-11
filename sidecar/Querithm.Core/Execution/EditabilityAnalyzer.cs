using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Execution;

/// <summary>
/// Decides whether a result set can be edited in the grid from the provider's exact column origins and
/// the cached schema. Exact or nothing: when anything is unknown the result set is read-only with the reason shown.
/// </summary>
public static class EditabilityAnalyzer
{
    public static ResultEditInfo Analyze(IDataEditDialect dialect, ResultColumn[] columns, ColumnOrigin?[]? origins, SchemaSnapshot? snapshot,
        Func<long, IndexInfo[]> indexes)
    {
        if (snapshot == null || snapshot.Objects.Length == 0)
            return ReadOnly("Read-only: no cached metadata for this database yet (open it in the explorer to load it)");
        if (origins == null) return ReadOnly("Read-only: column origins unknown for this batch");
        var visible = columns.Where(c => !c.Hidden && c.Ordinal >= 0).ToArray();
        var ids = visible.Select(c => At(origins, c.Ordinal)?.ObjectId).OfType<long>().Distinct().ToArray();
        if (ids.Length == 0) return ReadOnly("Read-only: the result has no table columns");
        var byId = snapshot.Objects.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        if (ids.Length > 1)
        {
            var names = ids.Select(i => byId.TryGetValue(i, out var o) ? $"{o.Schema}.{o.Name}" : $"#{i}").OrderBy(n => n, StringComparer.Ordinal);
            return ReadOnly($"Read-only: query reads more than one table ({string.Join(", ", names)})");
        }
        if (!byId.TryGetValue(ids[0], out var table)) return ReadOnly("Read-only: the table is not in the cached metadata (refresh the explorer)");
        var qualified = $"{table.Schema}.{table.Name}";
        if (table.Kind != ObjectKind.Table) return ReadOnly($"Read-only: {qualified} is a {KindName(table.Kind)}");
        // Apply is all or nothing through a transaction; a MyISAM / MEMORY / ARCHIVE / Aria table keeps what a failed Apply wrote.
        if (table.NoRollbackEngine is { } engine) return ReadOnly($"Read-only: {qualified} is a {engine} table — its changes can't be rolled back");
        if (table.Columns is not { Length: > 0 } tableColumns) return ReadOnly($"Read-only: column metadata for {qualified} is not loaded yet");

        SnapColumn? ColumnOf(int ordinal) =>
            At(origins, ordinal) is { } o && o.ObjectId == table.Id
                ? tableColumns.FirstOrDefault(x => x.Name == o.Column) ?? tableColumns.FirstOrDefault(x => string.Equals(x.Name, o.Column, StringComparison.OrdinalIgnoreCase))
                : null;

        // First result ordinal of each table column.
        var firstOrdinal = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in visible)
            if (ColumnOf(c.Ordinal) is { } sc) firstOrdinal.TryAdd(sc.Name, c.Ordinal);

        var tableIndexes = indexes(table.Id);
        string[] key;
        var primary = tableIndexes.FirstOrDefault(i => i.IsPrimary)?.Columns ?? tableColumns.Where(c => c.Pk).Select(c => c.Name).ToArray();
        if (primary.Length > 0)
        {
            var missing = primary.Where(k => !firstOrdinal.ContainsKey(k)).ToArray();
            if (missing.Length > 0)
                return ReadOnly($"Read-only: primary key column{(missing.Length > 1 ? "s" : "")} {string.Join(", ", missing)} {(missing.Length > 1 ? "are" : "is")} not in the SELECT list");
            key = primary;
        }
        else
        {
            var notNull = tableColumns.Where(c => !c.Nullable).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            // A partial / filtered, expression, disabled or invalid unique index does not prove a row unique.
            var uniques = tableIndexes.Where(i => i.IsUnique && !i.NotAKey && i.Columns.Length > 0 && i.Columns.All(notNull.Contains)).ToArray();
            var usable = uniques.FirstOrDefault(i => i.Columns.All(firstOrdinal.ContainsKey));
            if (usable == null)
                return ReadOnly(uniques.Length > 0
                    ? $"Read-only: {qualified} has no primary key, and the columns of unique index {uniques[0].Name} ({string.Join(", ", uniques[0].Columns)}) are not all in the SELECT list"
                    : $"Read-only: {qualified} has no primary key or NOT NULL unique index");
            key = usable.Columns;
        }
        foreach (var k in key)
        {
            var kc = tableColumns.First(c => c.Name == k);
            if (!dialect.ExactKey(kc.Type)) return ReadOnly($"Read-only: key column {k} is {kc.Type}, which the grid cannot match exactly");
            // Phase D0f: a key read as text without a fixed type cannot be matched back to its typed value.
            if (!dialect.EditsDynamicColumns && visible.FirstOrDefault(c => ColumnOf(c.Ordinal)?.Name == k) is { Dynamic: true })
                return ReadOnly($"Read-only: key column {k} has no fixed type in this result, so its values cannot be matched exactly");
        }

        var editColumns = new List<EditColumn>();
        foreach (var c in visible)
        {
            var sc = ColumnOf(c.Ordinal);
            if (sc == null)
            {
                editColumns.Add(new EditColumn { Ordinal = c.Ordinal, Locked = true, LockReason = "computed in the query, not a table column", Nullable = true });
                continue;
            }
            string? lockReason = null;
            if (firstOrdinal[sc.Name] != c.Ordinal) lockReason = $"{sc.Name} appears earlier in the result; edit it there";
            else if (c.Dynamic && !dialect.EditsDynamicColumns) lockReason = "its values have no fixed type here (shown as text): change it with an UPDATE statement";
            else if (sc.Computed) lockReason = "computed column";
            else if (dialect.LockReason(sc) is { } r) lockReason = r;
            else if (sc.Identity) lockReason = "identity values are generated by the server";
            editColumns.Add(new EditColumn
            {
                Ordinal = c.Ordinal, Column = sc.Name, Type = sc.Type, Nullable = sc.Nullable, HasDefault = sc.Default != null || sc.Identity,
                Generated = sc.Identity || sc.Computed, Locked = lockReason != null, LockReason = lockReason,
                Comparable = dialect.Comparable(sc.Type), Key = key.Contains(sc.Name),
            });
        }

        // Inserting needs every NOT NULL column without a default to be in the result (and writable).
        var required = tableColumns
            .Where(t => !t.Nullable && t.Default == null && !t.Identity && !t.Computed && dialect.LockReason(t) == null)
            .Where(t => !editColumns.Any(e => e.Column == t.Name && !e.Locked))
            .Select(t => t.Name).ToArray();
        var (tokenKind, tokenColumn) = dialect.Token(table);

        return new ResultEditInfo
        {
            Editable = true, ObjectId = table.Id, Schema = table.Schema, Table = table.Name, KeyColumns = key,
            KeyOrdinals = key.Select(k => firstOrdinal[k]).ToArray(), TokenKind = tokenKind, TokenColumn = tokenColumn,
            InsertBlockedReason = required.Length > 0
                ? $"Inserting needs NOT NULL column{(required.Length > 1 ? "s" : "")} {string.Join(", ", required)}, which {(required.Length > 1 ? "are" : "is")} not in the SELECT list"
                : null,
            ConcurrencyNote = dialect.ConcurrencyNote(tokenKind),
            Columns = editColumns.ToArray(),
        };
    }

    static ColumnOrigin? At(ColumnOrigin?[] origins, int ordinal) => ordinal >= 0 && ordinal < origins.Length ? origins[ordinal] : null;

    static ResultEditInfo ReadOnly(string reason) => new() { Editable = false, Reason = reason };

    static string KindName(ObjectKind k) => k switch
    {
        ObjectKind.View => "view",
        ObjectKind.Tvf or ObjectKind.Func => "function",
        ObjectKind.Proc => "procedure",
        ObjectKind.Synonym => "synonym",
        ObjectKind.Package => "package",
        ObjectKind.PackageBody => "package body",
        _ => k.ToString().ToLowerInvariant(),
    };
}
