using System.Data.Common;
using Microsoft.Data.Sqlite;
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
        // A table's indexes and triggers, grouped once (names folded: SQLite compares names case-insensitively). Before, each
        // table scanned every entry: 4,000 tables with an index each took 593 ms against 16 ms for 500 (audit M23).
        var dependents = entries.Where(d => d.Type is "index" or "trigger").ToLookup(d => (d.Schema, d.TableName.ToLowerInvariant()));
        // A view's columns come from the tables and views it reads, which SQLite does not record: its marker also covers
        // every table's and view's SQL, so a base table dropped and created again (or changed) reads the view again —
        // before, a view described while its table was missing kept no columns until a full refresh (M11).
        // Hashed once per read (only when there is a view): one view's marker then costs its own SQL, not every table's.
        // A view of main reads only main (SQLite refuses a reference to temp there), so its marker leaves temp out: a tab's
        // TEMP table changed every main view's marker in the shared store, and the next tab's refresh changed it back
        // (code audit 2026-11-25, H7 / L14). A temp view can read both.
        string? mainRelations = null, allRelations = null;
        string HashRelations(bool withTemp) => Hash(entries.Where(d => d.Type is "table" or "view" && (withTemp || d.Schema == "main"))
            .OrderBy(d => d.Schema, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal)
            .SelectMany(d => new[] { d.Schema, d.Name, d.Sql }).ToArray()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Relations(string schema) => schema == "temp" ? allRelations ??= HashRelations(withTemp: true) : mainRelations ??= HashRelations(withTemp: false);
        var stamps = new List<ObjectStamp>();
        foreach (var e in entries)
        {
            if (KindOf(e.Type) is not { } kind) continue;
            var marker = e.Sql;
            if (kind == ObjectKind.Table)
            {
                var deps = dependents[(e.Schema, e.Name.ToLowerInvariant())]
                    .OrderBy(d => d.Type, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal).Select(d => $"{d.Type}:{d.Name}:{d.Sql}");
                marker = string.Join("\n", deps.Prepend(e.Sql ?? ""));
                // Fourth round (L5): REFERENCES parent without columns names the parent's key, which is the parent's SQL:
                // a parent created, dropped or changed later changes the child's foreign key, so it changes its marker.
                foreach (var referenced in KeylessReferences(e.Sql))
                    marker += "\nref:" + (bySchemaTable.TryGetValue((e.Schema, referenced.ToLowerInvariant()), out var p) ? p.Sql : "\0none");
            }
            else if (kind == ObjectKind.View) marker = (e.Sql ?? "") + "\n" + Relations(e.Schema);
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
        // Only the objects asked for are read (audit M23: an incremental read of one table read every table's columns),
        // their names passed as JSON arrays (parameters, never SQL text).
        var asked = entries.Where(e => wanted.Contains(IdOf(e.Schema, e.Type, e.Name))).ToList();
        // Written with Utf8JsonWriter, not JsonSerializer: reflection serialization is a trim warning in this rooted
        // assembly (scripts/trim-warnings.mjs, code audit 2026-11-25 M5); each list is built once.
        var mainTables = Names(asked, "main", "table");
        var tempTables = Names(asked, "temp", "table");
        var columnAt = new Dictionary<(long, string), int>();
        var notes = new List<ObjectNote>();
        void AddColumn(long id, SqliteColumnRow row)
        {
            if (row.Pk > 0) (pkCols.TryGetValue(id, out var l) ? l : pkCols[id] = []).Add((row.Pk, row.Name));
            columnAt[(id, row.Name)] = columns.Count;
            columns.Add(new ColumnRow(id, row.Cid + 1, row.Name, row.Type, null, null, null, Nullable: !row.NotNull, Identity: false, Computed: row.Hidden is 2 or 3,
                Default: row.Default));
        }
        // Tables in one query per schema; a table's columns always read.
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT 'main', m.name, p.cid, p.name, p.type, p."notnull", p.dflt_value, p.pk, p.hidden FROM main.sqlite_schema m JOIN pragma_table_xinfo(m.name, 'main') p
                  WHERE m.type = 'table' AND m.name IN (SELECT value FROM json_each($mainTables))
                UNION ALL
                SELECT 'temp', m.name, p.cid, p.name, p.type, p."notnull", p.dflt_value, p.pk, p.hidden FROM temp.sqlite_schema m JOIN pragma_table_xinfo(m.name, 'temp') p
                  WHERE m.type = 'table' AND m.name IN (SELECT value FROM json_each($tempTables))
                """;
            cmd.Parameters.Add(new SqliteParameter("$mainTables", mainTables));
            cmd.Parameters.Add(new SqliteParameter("$tempTables", tempTables));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                if (ids.TryGetValue((r.GetString(0), r.GetString(1).ToLowerInvariant()), out var id))
                    AddColumn(id, SqliteColumnRow.Read(r));
        }
        // Views in one query first (code audit 2026-11-25, M6: one query per view, 500 views meant 500 commands); a view
        // whose table was dropped cannot be described, and its error fails that query (audit H14), so then one at a time.
        var views = asked.Where(e => e.Type == "view").ToList();
        if (views.Count > 1)
        {
            ct.ThrowIfCancellationRequested();
            List<(string Schema, string Name, SqliteColumnRow Row)>? rows = [];
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT 'main', m.name, p.cid, p.name, p.type, p."notnull", p.dflt_value, p.pk, p.hidden FROM main.sqlite_schema m JOIN pragma_table_xinfo(m.name, 'main') p
                      WHERE m.type = 'view' AND m.name IN (SELECT value FROM json_each($mainViews))
                    UNION ALL
                    SELECT 'temp', m.name, p.cid, p.name, p.type, p."notnull", p.dflt_value, p.pk, p.hidden FROM temp.sqlite_schema m JOIN pragma_table_xinfo(m.name, 'temp') p
                      WHERE m.type = 'view' AND m.name IN (SELECT value FROM json_each($tempViews))
                    ORDER BY 1, 2, 3
                    """;
                cmd.Parameters.Add(new SqliteParameter("$mainViews", Names(asked, "main", "view")));
                cmd.Parameters.Add(new SqliteParameter("$tempViews", Names(asked, "temp", "view")));
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false)) rows.Add((r.GetString(0), r.GetString(1), SqliteColumnRow.Read(r)));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteError) { rows = null; } // a broken view: one at a time
            if (rows != null)
            {
                foreach (var (schema, name, row) in rows)
                    if (ids.TryGetValue((schema, name.ToLowerInvariant()), out var id)) AddColumn(id, row);
                views.Clear();
            }
        }
        foreach (var view in views)
        {
            ct.ThrowIfCancellationRequested();
            var id = IdOf(view.Schema, view.Type, view.Name);
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 0, 0, p.cid, p.name, p.type, p.\"notnull\", p.dflt_value, p.pk, p.hidden FROM pragma_table_xinfo($name, $schema) p";
                cmd.Parameters.Add(new SqliteParameter("$name", view.Name));
                cmd.Parameters.Add(new SqliteParameter("$schema", view.Schema));
                var rows = new List<SqliteColumnRow>();
                await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false)) rows.Add(SqliteColumnRow.Read(r));
                foreach (var row in rows) AddColumn(id, row);
            }
            // SQLITE_ERROR only: the view names a table or column that no longer exists (audit H14). It is listed without
            // columns and says why (M11). A busy, locked or failing file is not that — it fails the read as before, so a
            // passing lock never leaves a healthy view without its columns.
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteError)
            {
                notes.Add(new ObjectNote(id, "invalid: its columns cannot be read (" + Reason(ex) + ")"));
            }
        }
        // A rowid alias (a single INTEGER PRIMARY KEY column) is never NULL and the engine numbers it.
        // Fourth round (M10): WITHOUT ROWID is read from the table's tokens after its column list, not found anywhere in its
        // SQL ('WITHOUT ROWID' in a default made a rowid table one; WITHOUT\nROWID was missed).
        var withoutRowid = new Dictionary<long, bool>();
        bool WithoutRowid(long id) => withoutRowid.TryGetValue(id, out var w) ? w : withoutRowid[id] = IsWithoutRowid(tableSql.GetValueOrDefault(id, ""));
        foreach (var (id, pks) in pkCols)
            if (pks.Count == 1 && columnAt.TryGetValue((id, pks[0].Name), out var i)
                && string.Equals(columns[i].TypeName, "INTEGER", StringComparison.OrdinalIgnoreCase) && !WithoutRowid(id))
                columns[i] = columns[i] with { Nullable = false, Identity = true };

        var indexes = new List<IndexRow>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT 'main', m.name, il.name, il."unique", il.origin, ii.seqno, ii.name, il.partial FROM main.sqlite_schema m
                  JOIN pragma_index_list(m.name, 'main') il JOIN pragma_index_info(il.name, 'main') ii WHERE m.type = 'table' AND m.name IN (SELECT value FROM json_each($mainTables))
                UNION ALL
                SELECT 'temp', m.name, il.name, il."unique", il.origin, ii.seqno, ii.name, il.partial FROM temp.sqlite_schema m
                  JOIN pragma_index_list(m.name, 'temp') il JOIN pragma_index_info(il.name, 'temp') ii WHERE m.type = 'table' AND m.name IN (SELECT value FROM json_each($tempTables))
                ORDER BY 1, 2, 3, 6
                """;
            cmd.Parameters.Add(new SqliteParameter("$mainTables", mainTables));
            cmd.Parameters.Add(new SqliteParameter("$tempTables", tempTables));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<(long Id, string Index, bool Unique, bool Pk, string? Column, bool Partial)>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                if (ids.TryGetValue((r.GetString(0), r.GetString(1).ToLowerInvariant()), out var id) && wanted.Contains(id))
                    rows.Add((id, r.GetString(2), r.GetInt32(3) == 1, r.GetString(4) == "pk", r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7) == 1));
            // Never a grid row key: a partial index (WHERE …) or one with an expression part (no column name).
            foreach (var g in rows.GroupBy(x => (x.Id, x.Index)))
                indexes.Add(new IndexRow(g.Key.Id, g.Key.Index, g.First().Unique, g.First().Pk, g.Select(x => x.Column ?? "(expression)").ToArray(),
                    NotAKey: g.First().Partial || g.Any(x => x.Column == null)));
        }
        // A rowid-alias primary key has no index of its own: the key is the declared PRIMARY KEY columns. (M11: a set, not a
        // scan of every index per table.)
        var withPrimary = indexes.Where(x => x.Primary).Select(x => x.ObjectId).ToHashSet();
        foreach (var (id, pks) in pkCols)
            if (!withPrimary.Contains(id))
                indexes.Add(new IndexRow(id, "PRIMARY KEY", true, true, pks.OrderBy(p => p.Pk).Select(p => p.Name).ToArray()));
        // Phase D1: an ordinary rowid table's PRIMARY KEY may hold NULLs (a SQLite legacy) unless its columns are NOT NULL;
        // such a key does not identify a row, so it is reported as a plain unique index, never as the primary key.
        for (var k = 0; k < indexes.Count; k++)
        {
            var ix = indexes[k];
            if (!ix.Primary) continue;
            var nullable = ix.Columns.Any(n => columnAt.TryGetValue((ix.ObjectId, n), out var at) && columns[at].Nullable);
            // WITHOUT ROWID tables enforce NOT NULL on their primary key themselves.
            if (nullable && !WithoutRowid(ix.ObjectId)) indexes[k] = ix with { Primary = false };
        }

        var fks = new List<FkRow>();
        var rowsFk = new List<(string Schema, long Id, string Table, int Fk, string Ref, string From, string? To)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT 'main', m.name, f.id, f.seq, f."table", f."from", f."to" FROM main.sqlite_schema m JOIN pragma_foreign_key_list(m.name, 'main') f
                  WHERE m.type = 'table' AND m.name IN (SELECT value FROM json_each($mainTables))
                UNION ALL
                SELECT 'temp', m.name, f.id, f.seq, f."table", f."from", f."to" FROM temp.sqlite_schema m JOIN pragma_foreign_key_list(m.name, 'temp') f
                  WHERE m.type = 'table' AND m.name IN (SELECT value FROM json_each($tempTables))
                ORDER BY 1, 2, 3, 4
                """;
            cmd.Parameters.Add(new SqliteParameter("$mainTables", mainTables));
            cmd.Parameters.Add(new SqliteParameter("$tempTables", tempTables));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                if (ids.TryGetValue((r.GetString(0), r.GetString(1).ToLowerInvariant()), out var id))
                    rowsFk.Add((r.GetString(0), id, r.GetString(1), r.GetInt32(2), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        }
        // REFERENCES parent without a column list means the parent's primary key, in its declared order (audit M26: the
        // pair came out with an empty column, and tier 1 offered the join `o.customer_id = c.""`).
        var parentKeys = new Dictionary<(string, string), string[]>();
        async Task<string[]> KeyOf(string schema, string table)
        {
            if (parentKeys.TryGetValue((schema, table.ToLowerInvariant()), out var known)) return known;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_table_info($name, $schema) WHERE pk > 0 ORDER BY pk";
            cmd.Parameters.Add(new SqliteParameter("$name", table));
            cmd.Parameters.Add(new SqliteParameter("$schema", schema));
            var names = new List<string>();
            try
            {
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false)) names.Add(r.GetString(0));
            }
            // A parent that cannot be described (a view over a dropped table, G#11): it has no key, so the FK is left out
            // instead of failing the whole read.
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteError) { names.Clear(); }
            return parentKeys[(schema, table.ToLowerInvariant())] = [.. names];
        }
        foreach (var g in rowsFk.GroupBy(x => (x.Id, x.Fk)))
        {
            var first = g.First();
            if (!ids.TryGetValue((first.Schema, first.Ref.ToLowerInvariant()), out var refId)) continue; // a reference to a missing table
            var list = g.ToList();
            string[] referenced;
            if (list.All(x => x.To != null)) referenced = list.Select(x => x.To!).ToArray();
            else
            {
                // M11: a parent read in this call already has its key in pkCols (one query per parent otherwise).
                referenced = wanted.Contains(refId)
                    ? (pkCols.TryGetValue(refId, out var known) ? known.OrderBy(p => p.Pk).Select(p => p.Name).ToArray() : [])
                    : await KeyOf(first.Schema, first.Ref).ConfigureAwait(false);
                // No primary key, or one of another width: SQLite reports a mismatch when the key is used; no pair is made up.
                if (referenced.Length != list.Count) continue;
            }
            var pairs = list.Select((x, i) => new FkColumnPair { Parent = x.From, Referenced = referenced[i] }).ToArray();
            fks.Add(new FkRow($"fk_{first.Table}_{first.Fk}", first.Id, refId, pairs));
        }
        return new MetadataBundle(columns, [], indexes, fks, Notes: notes);
    }

    /// <summary>
    /// The CREATE TABLE ends with the WITHOUT ROWID table option: read from its tokens after the column list's ')' (the
    /// options are comma-separated words there: WITHOUT ROWID, STRICT).
    /// </summary>
    internal static bool IsWithoutRowid(string sql)
    {
        if (sql.IndexOf("ROWID", StringComparison.OrdinalIgnoreCase) < 0) return false;
        var code = SqliteTokenizer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();
        var open = code.FindIndex(t => t.IsP("("));
        if (open < 0) return false;
        var depth = 0;
        var close = -1;
        for (var i = open; i < code.Count && close < 0; i++)
            if (code[i].IsP("(")) depth++;
            else if (code[i].IsP(")") && --depth == 0) close = i;
        for (var i = close + 1; close >= 0 && i + 1 < code.Count; i++)
            if (code[i].Is("WITHOUT") && code[i + 1].Is("ROWID")) return true;
        return false;
    }

    /// <summary>
    /// The tables a CREATE TABLE references without a column list (<c>REFERENCES parent</c>: the parent's primary key), by
    /// tokens; empty when its SQL names no REFERENCES.
    /// </summary>
    internal static IEnumerable<string> KeylessReferences(string? sql)
    {
        if (sql == null || sql.IndexOf("REFERENCES", StringComparison.OrdinalIgnoreCase) < 0) yield break;
        var code = SqliteTokenizer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();
        for (var i = 0; i + 1 < code.Count; i++)
        {
            if (!code[i].Is("REFERENCES")) continue;
            var name = i + 1;
            if (name + 2 < code.Count && code[name + 1].IsP(".")) name += 2; // schema.table
            if (name + 1 < code.Count && code[name + 1].IsP("(")) continue;
            yield return SqliteTokenizer.Unquote(code[name]);
        }
    }

    /// <summary>The names of the asked objects of one schema and type, as a JSON array (a parameter, never SQL text).</summary>
    static string Names(List<Entry> asked, string schema, string type)
    {
        using var buffer = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var e in asked)
                if (e.Schema == schema && e.Type == type) w.WriteStringValue(e.Name);
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>SQLITE_ERROR, the primary code of "no such table / column": a statement that cannot be prepared.</summary>
    const int SqliteError = 1;

    /// <summary>SQLite's own reason, without Microsoft.Data.Sqlite's "SQLite Error 1: '…'." wrapper, bounded (it is shown and stored).</summary>
    static string Reason(SqliteException ex)
    {
        var m = ex.Message;
        var open = m.IndexOf('\'');
        var close = m.LastIndexOf('\'');
        if (open >= 0 && close > open) m = m[(open + 1)..close];
        return m.Length > 200 ? m[..200] : m;
    }

    /// <summary>One row of pragma_table_xinfo, columns 2–8 of the detail queries.</summary>
    readonly record struct SqliteColumnRow(int Cid, string Name, string Type, bool NotNull, string? Default, int Pk, int Hidden)
    {
        public static SqliteColumnRow Read(DbDataReader r) =>
            new(r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), r.GetInt32(5) != 0, r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.GetInt32(8));
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
