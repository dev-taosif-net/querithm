using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using DuckDB.NET.Data;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.PgSql;

namespace Querithm.Providers.DuckDb;

/// <summary>
/// DuckDB catalog (Phase D3, §8): one query over duckdb_tables / views / indexes / sequences / functions gives every object
/// of the session's database and of its temp catalog; each marker hashes the object's own SQL — a table's with its indexes'
/// (foreign keys and checks are in the table's SQL), a sequence's without its START (which moves as values are drawn).
/// Temp objects exist only on the reading connection and are stamped session-local under the schema "temp" (DuckDB resolves
/// <c>temp.name</c> to its temp catalog). Object ids are stable hashes of (schema, kind, name). Details come from
/// duckdb_columns and duckdb_constraints in one query each.
/// </summary>
public sealed class DuckDbMetadata : IMetadataProvider
{
    internal sealed record Entry(string Schema, string Type, string Name, string? Parent, string? Sql, string? Params, long? Rows);

    /// <summary>The database (catalog) each connection opened on, read once at open: <c>USE</c> never changes what a snapshot lists.</summary>
    static readonly ConditionalWeakTable<DbConnection, string> Databases = new();

    internal static void Remember(DbConnection c, string database) => Databases.AddOrUpdate(c, database);

    internal static string DatabaseOf(DbConnection c) => Databases.TryGetValue(c, out var d) ? d : "memory";

    const string Filter = "database_name IN ($db, 'temp') AND NOT internal";

    static readonly string SchemaQuery = $"""
        SELECT CASE WHEN database_name = 'temp' THEN 'temp' ELSE schema_name END, 'table', table_name, NULL, sql, NULL, estimated_size FROM duckdb_tables() WHERE {Filter}
        UNION ALL
        SELECT CASE WHEN database_name = 'temp' THEN 'temp' ELSE schema_name END, 'view', view_name, NULL, sql, NULL, NULL FROM duckdb_views() WHERE {Filter}
        UNION ALL
        SELECT CASE WHEN database_name = 'temp' THEN 'temp' ELSE schema_name END, 'index', index_name, table_name, sql, NULL, NULL FROM duckdb_indexes() WHERE database_name IN ($db, 'temp')
        UNION ALL
        SELECT CASE WHEN database_name = 'temp' THEN 'temp' ELSE schema_name END, 'sequence', sequence_name, NULL, sql, NULL, NULL FROM duckdb_sequences() WHERE database_name IN ($db, 'temp')
        UNION ALL
        SELECT CASE WHEN database_name = 'temp' THEN 'temp' ELSE schema_name END, function_type, function_name, NULL, macro_definition, array_to_string(parameters, ', '), NULL
          FROM duckdb_functions() WHERE {Filter} AND function_type IN ('macro', 'table_macro')
        UNION ALL
        SELECT CASE WHEN database_name = 'temp' THEN 'temp' ELSE schema_name END, 'type', type_name, NULL,
               type_oid::VARCHAR || ':' || logical_type || ':' || coalesce(to_json(labels)::VARCHAR, ''), NULL, NULL
          FROM duckdb_types() WHERE {Filter}
        """;

    internal static async Task<List<Entry>> ReadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = SchemaQuery;
        cmd.Parameters.Add(new DuckDBParameter("db", DatabaseOf(c)));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Entry>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new Entry(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6)));
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

    static string Group(string type) => type switch { "view" => "table", "table_macro" => "macro", _ => type };

    internal static long IdOf(string schema, string type, string name) => Hash(schema, Group(type), name.ToLowerInvariant());

    static ObjectKind? KindOf(string type) => type switch
    {
        "table" => ObjectKind.Table, "view" => ObjectKind.View, "sequence" => ObjectKind.Sequence, "macro" => ObjectKind.Func, "table_macro" => ObjectKind.Tvf,
        "type" => ObjectKind.Type, _ => null,
    };

    /// <summary>A sequence's SQL without "START n": DuckDB rewrites START as values are drawn.</summary>
    static string SequenceMarker(string? sql)
    {
        var code = DuckDbTokenizer.Tokenize(sql ?? "").Where(t => !t.IsTrivia).ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].Is("START")) { i += i + 1 < code.Count && code[i + 1].Is("WITH") ? 2 : 1; continue; }
            sb.Append(code[i].Text).Append(' ');
        }
        return sb.ToString();
    }

    public async Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection connection, CancellationToken ct)
    {
        var entries = await ReadSchemaAsync(connection, ct).ConfigureAwait(false);
        var stamps = new List<ObjectStamp>();
        // Macros may be overloaded: one object per name, its marker over every overload.
        foreach (var g in entries.Where(e => KindOf(e.Type) != null).GroupBy(e => (e.Schema, Group(e.Type), e.Name.ToLowerInvariant())))
        {
            var e = g.First();
            var kind = KindOf(e.Type)!.Value;
            var marker = kind switch
            {
                ObjectKind.Table => string.Join("\n", entries
                    .Where(d => d.Type == "index" && d.Schema == e.Schema && string.Equals(d.Parent, e.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => d.Sql).Prepend(e.Sql)),
                ObjectKind.Sequence => SequenceMarker(e.Sql),
                _ => string.Join("\n", g.Select(x => $"{x.Params}|{x.Sql}").Order(StringComparer.Ordinal)),
            };
            stamps.Add(new ObjectStamp(IdOf(e.Schema, e.Type, e.Name), e.Schema, e.Name, kind, Hash(marker), null, null, SessionLocal: e.Schema == "temp"));
        }
        return stamps;
    }

    public async Task<MetadataBundle> GetDetailsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var wanted = objectIds.ToHashSet();
        var db = DatabaseOf(connection);
        var columns = new List<ColumnRow>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT CASE WHEN c.database_name = 'temp' THEN 'temp' ELSE c.schema_name END, c.table_name, v.view_name IS NOT NULL, c.column_index, c.column_name,
                       c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.is_nullable, c.column_default
                  FROM duckdb_columns() c
                  LEFT JOIN duckdb_views() v ON v.database_name = c.database_name AND v.schema_name = c.schema_name AND v.view_name = c.table_name
                 WHERE c.database_name IN ($db, 'temp') AND NOT c.internal
                 ORDER BY 1, 2, 4
                """;
            cmd.Parameters.Add(new DuckDBParameter("db", db));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = IdOf(r.GetString(0), r.GetBoolean(2) ? "view" : "table", r.GetString(1));
                if (!wanted.Contains(id)) continue;
                var type = r.GetString(5);
                // DECIMAL's precision and scale are in its type text; the binary precision of integers is not shown.
                var isDecimal = type.StartsWith("DECIMAL", StringComparison.OrdinalIgnoreCase);
                columns.Add(new ColumnRow(id, r.GetInt32(3), r.GetString(4), type, r.IsDBNull(6) ? null : r.GetInt32(6),
                    isDecimal && !r.IsDBNull(7) ? r.GetInt32(7) : null, isDecimal && !r.IsDBNull(8) ? r.GetInt32(8) : null,
                    Nullable: r.GetBoolean(9), Identity: false, Computed: false, Default: r.IsDBNull(10) ? null : r.GetString(10)));
            }
        }

        var indexes = new List<IndexRow>();
        var fks = new List<FkRow>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT CASE WHEN database_name = 'temp' THEN 'temp' ELSE schema_name END, table_name, constraint_type, constraint_name, constraint_column_names,
                       referenced_table, referenced_column_names
                  FROM duckdb_constraints() WHERE database_name IN ($db, 'temp') AND constraint_type IN ('PRIMARY KEY', 'UNIQUE', 'FOREIGN KEY')
                 ORDER BY 1, 2, constraint_index
                """;
            cmd.Parameters.Add(new DuckDBParameter("db", db));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var schema = r.GetString(0);
                var id = IdOf(schema, "table", r.GetString(1));
                if (!wanted.Contains(id)) continue;
                var cols = Strings(r.GetValue(4));
                switch (r.GetString(2))
                {
                    case "PRIMARY KEY":
                        indexes.Add(new IndexRow(id, r.GetString(3), true, true, cols));
                        break;
                    case "UNIQUE":
                        indexes.Add(new IndexRow(id, r.GetString(3), true, false, cols));
                        break;
                    case "FOREIGN KEY" when !r.IsDBNull(5):
                        // DuckDB keeps a foreign key inside one schema.
                        var refs = Strings(r.GetValue(6));
                        fks.Add(new FkRow(r.GetString(3), id, IdOf(schema, "table", r.GetString(5)),
                            cols.Select((c, i) => new FkColumnPair { Parent = c, Referenced = i < refs.Length ? refs[i] : "" }).ToArray()));
                        break;
                }
            }
        }
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT CASE WHEN database_name = 'temp' THEN 'temp' ELSE schema_name END, table_name, index_name, is_unique, expressions
                  FROM duckdb_indexes() WHERE database_name IN ($db, 'temp') ORDER BY 1, 2, 3
                """;
            cmd.Parameters.Add(new DuckDBParameter("db", db));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = IdOf(r.GetString(0), "table", r.GetString(1));
                if (!wanted.Contains(id)) continue;
                var cols = IndexColumns(r.IsDBNull(4) ? "" : r.GetString(4));
                // An expression key part (lower(x)) is never a grid row key.
                indexes.Add(new IndexRow(id, r.GetString(2), r.GetBoolean(3), false, cols, NotAKey: cols.Contains("(expression)")));
            }
        }

        var parameters = new List<ParamRow>();
        foreach (var e in (await ReadSchemaAsync(connection, ct).ConfigureAwait(false)).Where(e => e.Type is "macro" or "table_macro"))
        {
            var id = IdOf(e.Schema, e.Type, e.Name);
            if (!wanted.Contains(id) || parameters.Any(p => p.ObjectId == id)) continue;
            var names = (e.Params ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < names.Length; i++) parameters.Add(new ParamRow(id, i + 1, names[i], "", null, false, false));
        }
        return new MetadataBundle(columns, parameters, indexes, fks);
    }

    static string[] Strings(object v) => v is System.Collections.IEnumerable e and not string ? e.Cast<object?>().Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray() : [];

    /// <summary>duckdb_indexes().expressions is a list printed as text ("[x, lower(y)]"): column names, or "(expression)".</summary>
    internal static string[] IndexColumns(string expressions)
    {
        var code = DuckDbTokenizer.Tokenize(expressions).Where(t => !t.IsTrivia).ToList();
        if (code.Count >= 2 && code[0].Text == "[" && code[^1].Text == "]") code = code.GetRange(1, code.Count - 2);
        var items = new List<List<PgToken>> { new() };
        var depth = 0;
        foreach (var t in code)
        {
            if (t.Text == "(") depth++;
            else if (t.Text == ")") depth--;
            if (t.Text == "," && depth == 0) { items.Add([]); continue; }
            items[^1].Add(t);
        }
        return items.Where(i => i.Count > 0).Select(i => i.Count == 1 && i[0].Kind is PgTokenKind.Word or PgTokenKind.QuotedIdent ? DuckDbTokenizer.Unquote(i[0]) : "(expression)").ToArray();
    }

    /// <summary>A macro's CREATE statement from its parameters and body (DuckDB stores only the body).</summary>
    static string MacroSql(Entry e) =>
        $"CREATE MACRO {DuckDbTokenizer.Quote(e.Schema)}.{DuckDbTokenizer.Quote(e.Name)}({e.Params}) AS {(e.Type == "table_macro" ? "TABLE " : "")}{e.Sql};";

    internal static string? SqlOf(Entry e) => e.Type switch { "macro" or "table_macro" => MacroSql(e), "type" => null, _ => e.Sql };

    /// <summary>
    /// A user type's CREATE statement: an ENUM from its labels; any other type from the logical type DuckDB resolves it to
    /// (<c>typeof(NULL::name)</c>) — DuckDB stores no SQL for types.
    /// </summary>
    internal static async Task<string?> TypeSqlAsync(DbConnection c, Entry e, CancellationToken ct)
    {
        var temp = e.Schema == "temp";
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT logical_type, labels FROM duckdb_types() WHERE database_name = $db AND schema_name = $schema AND type_name = $name AND NOT internal";
        cmd.Parameters.Add(new DuckDBParameter("db", temp ? "temp" : DatabaseOf(c)));
        cmd.Parameters.Add(new DuckDBParameter("schema", temp ? "main" : e.Schema));
        cmd.Parameters.Add(new DuckDBParameter("name", e.Name));
        string? logical = null;
        string[] labels = [];
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
            logical = r.GetString(0);
            if (!r.IsDBNull(1)) labels = Strings(r.GetValue(1));
        }
        var name = $"{DuckDbTokenizer.Quote(e.Schema)}.{DuckDbTokenizer.Quote(e.Name)}";
        if (logical == "ENUM") return $"CREATE TYPE {name} AS ENUM ({string.Join(", ", labels.Select(l => "'" + l.Replace("'", "''") + "'"))});";
        await using var t = c.CreateCommand();
        t.CommandText = $"SELECT typeof(NULL::{name})";
        return $"CREATE TYPE {name} AS {Convert.ToString(await t.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture)};";
    }

    internal static async Task<string?> DefinitionOf(DbConnection c, Entry e, CancellationToken ct) => e.Type == "type" ? await TypeSqlAsync(c, e, ct).ConfigureAwait(false) : SqlOf(e);

    public async Task<string?> GetDefinitionAsync(DbConnection connection, ObjectRef obj, CancellationToken ct)
    {
        var entries = await ReadSchemaAsync(connection, ct).ConfigureAwait(false);
        var e = entries.FirstOrDefault(e => e.Type != "index" && (obj.ObjectId is { } id ? IdOf(e.Schema, e.Type, e.Name) == id
            : string.Equals(e.Schema, obj.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Name, obj.Name, StringComparison.OrdinalIgnoreCase)));
        return e == null ? null : await DefinitionOf(connection, e, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var wanted = objectIds.ToHashSet();
        var list = new List<(long Id, string? Definition)>();
        foreach (var e in (await ReadSchemaAsync(connection, ct).ConfigureAwait(false)).Where(e => e.Type != "index").DistinctBy(e => IdOf(e.Schema, e.Type, e.Name)))
        {
            var id = IdOf(e.Schema, e.Type, e.Name);
            if (wanted.Contains(id)) list.Add((id, await DefinitionOf(connection, e, ct).ConfigureAwait(false)));
        }
        return list;
    }

    /// <summary>
    /// Names a view or macro reads, from its SQL tokens: identifiers in table positions (after FROM / JOIN), and other
    /// identifiers that are not a function call, a qualified member or an alias. Names compare case-insensitively.
    /// </summary>
    static HashSet<string> Named(string? sql)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sql == null) return set;
        var toks = DuckDbTokenizer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind is not (PgTokenKind.Word or PgTokenKind.QuotedIdent) || (i > 0 && toks[i - 1].Text == ".")) continue;
            var prev = i > 0 ? toks[i - 1].Text.ToUpperInvariant() : "";
            var tablePosition = prev is "FROM" or "JOIN";
            if (!tablePosition && (prev == "AS" || (i + 1 < toks.Count && toks[i + 1].Text == "("))) continue;
            set.Add(DuckDbTokenizer.Unquote(t));
        }
        return set;
    }

    public async Task<Dependency[]> GetDependenciesAsync(DbConnection connection, ObjectRef obj, CancellationToken ct)
    {
        var entries = (await ReadSchemaAsync(connection, ct).ConfigureAwait(false)).Where(e => KindOf(e.Type) != null).ToList();
        Dependency Dep(DependencyDirection d, Entry e) =>
            new() { Direction = d, Object = new ObjectRef { Schema = e.Schema, Name = e.Name, Kind = KindOf(e.Type)!.Value, ObjectId = IdOf(e.Schema, e.Type, e.Name) } };
        var self = entries.FirstOrDefault(e => e.Schema == obj.Schema && string.Equals(e.Name, obj.Name, StringComparison.OrdinalIgnoreCase));
        if (self == null) return [];
        var list = new List<Dependency>();
        if (self.Type is "view" or "macro" or "table_macro")
        {
            var named = Named(self.Sql);
            foreach (var rel in entries.Where(r => r.Type is "table" or "view" && r.Schema == self.Schema && r != self && named.Contains(r.Name)))
                list.Add(Dep(DependencyDirection.Uses, rel));
        }
        if (self.Type is "table" or "view")
            foreach (var m in entries.Where(e => e.Type is "view" or "macro" or "table_macro" && e.Schema == self.Schema && e != self))
                if (Named(m.Sql).Contains(self.Name)) list.Add(Dep(DependencyDirection.UsedBy, m));
        return list.DistinctBy(d => (d.Direction, d.Object.ObjectId)).ToArray();
    }

    /// <summary>A view or macro opens as CREATE OR REPLACE (DuckDB replaces either in place); anything else as written.</summary>
    public string ToEditableScript(string definition, Capabilities caps)
    {
        var code = DuckDbTokenizer.Tokenize(definition).Where(t => !t.IsTrivia).ToList();
        if (code.Count < 3 || !code[0].Is("CREATE") || code[1].Is("OR")) return definition;
        var at = 1;
        if (code[at].Is("TEMP") || code[at].Is("TEMPORARY")) at++;
        if (!(code[at].Is("VIEW") || code[at].Is("MACRO") || code[at].Is("FUNCTION"))) return definition;
        var insert = code[0].Offset + code[0].Length;
        var text = definition[..insert] + " OR REPLACE" + definition[insert..];
        return text.TrimEnd().EndsWith(';') ? text : text.TrimEnd() + ";";
    }

    public async Task<RowCount[]> GetRowCountsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var wanted = objectIds.ToHashSet();
        return (await ReadSchemaAsync(connection, ct).ConfigureAwait(false)).Where(e => e.Type == "table")
            .Select(e => (Id: IdOf(e.Schema, e.Type, e.Name), e.Rows)).Where(x => wanted.Contains(x.Id))
            .Select(x => new RowCount { ObjectId = x.Id, Rows = x.Rows, Estimated = true }).ToArray();
    }
}

/// <summary>Script as CREATE / DROP / DROP and CREATE / SELECT from DuckDB's own stored SQL; identifiers quoted by the dialect.</summary>
public sealed class DuckDbScripter : IObjectScripter
{
    public async Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, CancellationToken ct)
    {
        var e = (await DuckDbMetadata.ReadSchemaAsync(connection, ct).ConfigureAwait(false))
                    .FirstOrDefault(x => x.Type != "index" && string.Equals(x.Schema, obj.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, obj.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{obj.Schema}.{obj.Name} no longer exists.");
        var name = $"{DuckDbTokenizer.Quote(e.Schema)}.{DuckDbTokenizer.Quote(e.Name)}";
        var create = ((await DuckDbMetadata.DefinitionOf(connection, e, ct).ConfigureAwait(false)) ?? "").TrimEnd().TrimEnd(';') + ";";
        var word = e.Type switch { "table_macro" => "MACRO TABLE", "macro" => "MACRO", _ => e.Type.ToUpperInvariant() };
        var drop = $"DROP {word} {name};";
        return kind switch
        {
            ScriptKind.Create => create,
            ScriptKind.Drop => drop,
            ScriptKind.DropCreate => $"{drop}\n{create}",
            ScriptKind.SelectTop when e.Type is "table" or "view" => $"SELECT *\nFROM {name}\nLIMIT 100;",
            _ => throw new NotSupportedException($"DuckDB has no {kind.ToString().ToUpperInvariant()} script for a {e.Type}."),
        };
    }
}
