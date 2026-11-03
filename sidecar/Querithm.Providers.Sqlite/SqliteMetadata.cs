using System.Data.Common;
using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Sqlite;

namespace Querithm.Providers.Sqlite;

/// <summary>
/// SQLite catalog (Phase D1, §8): one query over main.sqlite_schema and temp.sqlite_schema gives every object; its marker
/// hashes the object's own SQL plus, for a table, the SQL of its indexes and triggers (foreign keys live in the table's
/// SQL), so any DDL that touches it changes the marker — computed in the sidecar from that one result, never per object.
/// Object ids are stable hashes of (schema, type, name). Objects of "temp" exist only on the reading connection and are
/// stamped session-local (D0d). Details come from the table-valued pragmas in one query per kind.
/// </summary>
public sealed class SqliteMetadata : IMetadataProvider
{
    internal sealed record Entry(string Schema, string Type, string Name, string TableName, string? Sql);

    const string SchemaQuery = """
        SELECT 'main', type, name, tbl_name, sql FROM main.sqlite_schema WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\'
        UNION ALL
        SELECT 'temp', type, name, tbl_name, sql FROM temp.sqlite_schema WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\'
        """;

    internal static async Task<List<Entry>> ReadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = SchemaQuery;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Entry>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new Entry(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    /// <summary>FNV-1a over UTF-8, 63 bits: a stable, positive id or marker.</summary>
    internal static long Hash(params string?[] parts)
    {
        var h = 14695981039346656037UL;
        foreach (var part in parts)
        {
            foreach (var b in Encoding.UTF8.GetBytes(part ?? "\0null"))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            h ^= 0x1F; h *= 1099511628211UL;
        }
        return (long)(h & long.MaxValue);
    }

    internal static long IdOf(string schema, string type, string name) => Hash(schema, type == "view" ? "table" : type, name.ToLowerInvariant());

    static ObjectKind? KindOf(string type) => type switch { "table" => ObjectKind.Table, "view" => ObjectKind.View, "trigger" => ObjectKind.Trigger, _ => null };

    public async Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection connection, CancellationToken ct)
    {
        var entries = await ReadSchemaAsync(connection, ct).ConfigureAwait(false);
        var bySchemaTable = entries.Where(e => e.Type is "table" or "view")
            .ToDictionary(e => (e.Schema, e.Name.ToLowerInvariant()), e => e);
        var stamps = new List<ObjectStamp>();
        foreach (var e in entries)
        {
            if (KindOf(e.Type) is not { } kind) continue;
            var marker = e.Sql;
            if (kind == ObjectKind.Table)
            {
                // A table's indexes and triggers change with it (names folded: SQLite compares names case-insensitively).
                var deps = entries.Where(d => d.Schema == e.Schema && d.Type is "index" or "trigger" && string.Equals(d.TableName, e.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d.Type, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal).Select(d => $"{d.Type}:{d.Name}:{d.Sql}");
                marker = string.Join("\n", deps.Prepend(e.Sql ?? ""));
            }
            long? parent = kind == ObjectKind.Trigger && bySchemaTable.TryGetValue((e.Schema, e.TableName.ToLowerInvariant()), out var owner)
                ? IdOf(owner.Schema, owner.Type, owner.Name) : null;
            stamps.Add(new ObjectStamp(IdOf(e.Schema, e.Type, e.Name), e.Schema, e.Name, kind, Hash(marker), null, parent, SessionLocal: e.Schema == "temp"));
        }
        return stamps;
    }

    public async Task<MetadataBundle> GetDetailsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var wanted = objectIds.ToHashSet();
        var entries = (await ReadSchemaAsync(connection, ct).ConfigureAwait(false)).Where(e => e.Type is "table" or "view").ToList();
        var ids = entries.ToDictionary(e => (e.Schema, e.Name.ToLowerInvariant()), e => IdOf(e.Schema, e.Type, e.Name));
        var columns = new List<ColumnRow>();
        var pkCols = new Dictionary<long, List<(int Pk, string Name)>>();
        var tableSql = entries.ToDictionary(e => IdOf(e.Schema, e.Type, e.Name), e => e.Sql ?? "");
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT 'main', m.name, p.cid, p.name, p.type, p."notnull", p.dflt_value, p.pk, p.hidden FROM main.sqlite_schema m JOIN pragma_table_xinfo(m.name, 'main') p
                  WHERE m.type IN ('table','view') AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
                UNION ALL
                SELECT 'temp', m.name, p.cid, p.name, p.type, p."notnull", p.dflt_value, p.pk, p.hidden FROM temp.sqlite_schema m JOIN pragma_table_xinfo(m.name, 'temp') p
                  WHERE m.type IN ('table','view') AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!ids.TryGetValue((r.GetString(0), r.GetString(1).ToLowerInvariant()), out var id) || !wanted.Contains(id)) continue;
                var name = r.GetString(3);
                var type = r.IsDBNull(4) ? "" : r.GetString(4);
                var pk = r.GetInt32(7);
                var hidden = r.GetInt32(8);
                if (pk > 0) (pkCols.TryGetValue(id, out var l) ? l : pkCols[id] = []).Add((pk, name));
                columns.Add(new ColumnRow(id, r.GetInt32(2) + 1, name, type, null, null, null, Nullable: r.GetInt32(5) == 0, Identity: false, Computed: hidden is 2 or 3,
                    Default: r.IsDBNull(6) ? null : r.GetString(6)));
            }
        }
        // A rowid alias (a single INTEGER PRIMARY KEY column) is never NULL and the engine numbers it.
        foreach (var (id, pks) in pkCols)
            if (pks.Count == 1 && columns.FindIndex(c => c.ObjectId == id && c.Name == pks[0].Name) is var i and >= 0
                && string.Equals(columns[i].TypeName, "INTEGER", StringComparison.OrdinalIgnoreCase) && !tableSql[id].Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase))
                columns[i] = columns[i] with { Nullable = false, Identity = true };

        var indexes = new List<IndexRow>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT 'main', m.name, il.name, il."unique", il.origin, ii.seqno, ii.name FROM main.sqlite_schema m
                  JOIN pragma_index_list(m.name, 'main') il JOIN pragma_index_info(il.name, 'main') ii WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
                UNION ALL
                SELECT 'temp', m.name, il.name, il."unique", il.origin, ii.seqno, ii.name FROM temp.sqlite_schema m
                  JOIN pragma_index_list(m.name, 'temp') il JOIN pragma_index_info(il.name, 'temp') ii WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
                ORDER BY 1, 2, 3, 6
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<(long Id, string Index, bool Unique, bool Pk, string? Column)>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                if (ids.TryGetValue((r.GetString(0), r.GetString(1).ToLowerInvariant()), out var id) && wanted.Contains(id))
                    rows.Add((id, r.GetString(2), r.GetInt32(3) == 1, r.GetString(4) == "pk", r.IsDBNull(6) ? null : r.GetString(6)));
            foreach (var g in rows.GroupBy(x => (x.Id, x.Index)))
                indexes.Add(new IndexRow(g.Key.Id, g.Key.Index, g.First().Unique, g.First().Pk, g.Select(x => x.Column ?? "(expression)").ToArray()));
        }
        // A rowid-alias primary key has no index of its own: the key is the declared PRIMARY KEY columns.
        foreach (var (id, pks) in pkCols)
            if (!indexes.Any(x => x.ObjectId == id && x.Primary))
                indexes.Add(new IndexRow(id, "PRIMARY KEY", true, true, pks.OrderBy(p => p.Pk).Select(p => p.Name).ToArray()));
        // Phase D1: an ordinary rowid table's PRIMARY KEY may hold NULLs (a SQLite legacy) unless its columns are NOT NULL;
        // such a key does not identify a row, so it is reported as a plain unique index, never as the primary key.
        for (var k = 0; k < indexes.Count; k++)
        {
            var ix = indexes[k];
            if (!ix.Primary) continue;
            var nullable = ix.Columns.Any(n => columns.FirstOrDefault(c => c.ObjectId == ix.ObjectId && c.Name == n) is { Nullable: true });
            // WITHOUT ROWID tables enforce NOT NULL on their primary key themselves.
            if (nullable && !tableSql.GetValueOrDefault(ix.ObjectId, "").Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase)) indexes[k] = ix with { Primary = false };
        }

        var fks = new List<FkRow>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT 'main', m.name, f.id, f.seq, f."table", f."from", f."to" FROM main.sqlite_schema m JOIN pragma_foreign_key_list(m.name, 'main') f
                  WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
                UNION ALL
                SELECT 'temp', m.name, f.id, f.seq, f."table", f."from", f."to" FROM temp.sqlite_schema m JOIN pragma_foreign_key_list(m.name, 'temp') f
                  WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
                ORDER BY 1, 2, 3, 4
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<(string Schema, long Id, string Table, int Fk, string Ref, string From, string? To)>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                if (ids.TryGetValue((r.GetString(0), r.GetString(1).ToLowerInvariant()), out var id) && wanted.Contains(id))
                    rows.Add((r.GetString(0), id, r.GetString(1), r.GetInt32(2), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
            foreach (var g in rows.GroupBy(x => (x.Id, x.Fk)))
            {
                var first = g.First();
                if (!ids.TryGetValue((first.Schema, first.Ref.ToLowerInvariant()), out var refId)) continue; // a reference to a missing table
                var pairs = g.Select(x => new FkColumnPair { Parent = x.From, Referenced = x.To ?? "" }).ToArray();
                fks.Add(new FkRow($"fk_{first.Table}_{first.Fk}", first.Id, refId, pairs));
            }
        }
        return new MetadataBundle(columns, [], indexes, fks);
    }

    public async Task<string?> GetDefinitionAsync(DbConnection connection, ObjectRef obj, CancellationToken ct)
    {
        var entries = await ReadSchemaAsync(connection, ct).ConfigureAwait(false);
        return entries.FirstOrDefault(e => (obj.ObjectId is { } id ? IdOf(e.Schema, e.Type, e.Name) == id
            : string.Equals(e.Schema, obj.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Name, obj.Name, StringComparison.OrdinalIgnoreCase)))?.Sql;
    }

    public async Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var wanted = objectIds.ToHashSet();
        return (await ReadSchemaAsync(connection, ct).ConfigureAwait(false))
            .Select(e => (Id: IdOf(e.Schema, e.Type, e.Name), e.Sql)).Where(x => wanted.Contains(x.Id)).Select(x => (x.Id, x.Sql)).ToList();
    }

    /// <summary>
    /// SQLite keeps no dependency catalog: a view or trigger uses the tables and views its stored SQL names (tokens that are
    /// identifiers, not calls, after the header), and a trigger uses its table. Names compare case-insensitively, as in
    /// SQLite; an object of the same schema (main or the session's temp) is what a bare name means.
    /// </summary>
    public async Task<Dependency[]> GetDependenciesAsync(DbConnection connection, ObjectRef obj, CancellationToken ct)
    {
        var entries = await ReadSchemaAsync(connection, ct).ConfigureAwait(false);
        var relations = entries.Where(e => e.Type is "table" or "view").ToList();
        static ObjectKind KindOf(Entry e) => e.Type switch { "view" => ObjectKind.View, "trigger" => ObjectKind.Trigger, _ => ObjectKind.Table };
        Dependency Dep(DependencyDirection d, Entry e) =>
            new() { Direction = d, Object = new ObjectRef { Schema = e.Schema, Name = e.Name, Kind = KindOf(e), ObjectId = IdOf(e.Schema, e.Type, e.Name) } };
        // The relations a module's SQL names: identifiers in a table position (after FROM / JOIN / INTO / UPDATE / ON / TABLE),
        // or anywhere else unless they are a call (followed by '('), a column (after '.') or an alias (after AS).
        HashSet<string> Named(Entry module)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (module.Sql == null) return set;
            var toks = SqliteTokenizer.Tokenize(module.Sql).Where(t => !t.IsTrivia).ToList();
            var start = toks.FindIndex(t => t.Kind == SqliteTokenKind.Word && (t.Text.Equals("AS", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)));
            for (var i = Math.Max(0, start); i < toks.Count; i++)
            {
                var t = toks[i];
                if (t.Kind is not (SqliteTokenKind.Word or SqliteTokenKind.QuotedIdent) || (i > 0 && toks[i - 1].Text == ".")) continue;
                var prev = i > 0 ? toks[i - 1].Text.ToUpperInvariant() : "";
                var tablePosition = prev is "FROM" or "JOIN" or "INTO" or "UPDATE" or "ON" or "TABLE";
                if (!tablePosition && (prev == "AS" || (i + 1 < toks.Count && toks[i + 1].Text == "("))) continue;
                set.Add(SqliteTokenizer.Unquote(t));
            }
            return set;
        }
        var self = entries.FirstOrDefault(e => e.Schema == obj.Schema && string.Equals(e.Name, obj.Name, StringComparison.OrdinalIgnoreCase) && e.Type != "index");
        if (self == null) return [];
        var list = new List<Dependency>();
        if (self.Type is "view" or "trigger")
        {
            var named = Named(self);
            foreach (var rel in relations.Where(r => r.Schema == self.Schema && !string.Equals(r.Name, self.Name, StringComparison.OrdinalIgnoreCase) && named.Contains(r.Name)))
                list.Add(Dep(DependencyDirection.Uses, rel));
            if (self.Type == "trigger" && relations.FirstOrDefault(r => r.Schema == self.Schema && string.Equals(r.Name, self.TableName, StringComparison.OrdinalIgnoreCase)) is { } table
                && list.All(d => !string.Equals(d.Object.Name, table.Name, StringComparison.OrdinalIgnoreCase)))
                list.Add(Dep(DependencyDirection.Uses, table));
        }
        if (self.Type is "table" or "view")
            foreach (var m in entries.Where(e => e.Type is "view" or "trigger" && e.Schema == self.Schema && e != self))
                if (Named(m).Contains(self.Name) || (m.Type == "trigger" && string.Equals(m.TableName, self.Name, StringComparison.OrdinalIgnoreCase)))
                    list.Add(Dep(DependencyDirection.UsedBy, m));
        return list.ToArray();
    }

    /// <summary>SQLite has no CREATE OR REPLACE: a view or trigger is edited as DROP … IF EXISTS + CREATE in one script.</summary>
    public string ToEditableScript(string definition, Capabilities caps)
    {
        var code = SqliteTokenizer.Tokenize(definition).Where(t => !t.IsTrivia).ToList();
        if (code.Count < 3 || !code[0].Is("CREATE")) return definition;
        var at = 1;
        if (code[at].Is("TEMP") || code[at].Is("TEMPORARY")) at++;
        var kind = code[at].Text.ToUpperInvariant();
        if (kind is not ("VIEW" or "TRIGGER")) return definition;
        var nameAt = at + 1;
        if (code.Count > nameAt + 2 && code[nameAt].Is("IF")) nameAt += 3; // IF NOT EXISTS
        var name = code.Count > nameAt + 2 && code[nameAt + 1].IsP(".") ? $"{code[nameAt].Text}.{code[nameAt + 2].Text}" : code[nameAt].Text;
        return $"DROP {kind} IF EXISTS {name};\n{definition.TrimEnd().TrimEnd(';')};";
    }

    /// <summary>Row counts come from sqlite_stat1 (after ANALYZE) and are estimates; unknown otherwise (no table scans).</summary>
    public async Task<RowCount[]> GetRowCountsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var wanted = objectIds.ToHashSet();
        var stats = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT tbl, stat FROM main.sqlite_stat1 WHERE idx IS NULL OR idx = tbl OR stat IS NOT NULL";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                if (!r.IsDBNull(1) && long.TryParse(r.GetString(1).Split(' ')[0], out var n) && !stats.ContainsKey(r.GetString(0))) stats[r.GetString(0)] = n;
        }
        catch (DbException) { /* no sqlite_stat1 before ANALYZE */ }
        var tables = (await ReadSchemaAsync(connection, ct).ConfigureAwait(false)).Where(e => e.Type == "table" && e.Schema == "main");
        return tables.Select(e => (Id: IdOf(e.Schema, e.Type, e.Name), e.Name)).Where(x => wanted.Contains(x.Id))
            .Select(x => new RowCount { ObjectId = x.Id, Rows = stats.TryGetValue(x.Name, out var n) ? n : null, Estimated = true }).ToArray();
    }
}
