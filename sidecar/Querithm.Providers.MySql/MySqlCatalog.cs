using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.MySql;

namespace Querithm.Providers.MySql;

/// <summary>
/// MySQL / MariaDB catalog (Phase D2, §8): one information_schema query per refresh gives every table, view, routine and
/// trigger of the session's database with a marker the server computes — SHA-256 (MySQL 9 has no MD5()) over the columns, indexes, foreign keys and
/// triggers of a table, or a module's definition — because UPDATE_TIME and CREATE_TIME are not reliable change markers.
/// Ids are stable hashes of (schema, kind, name); names are kept exactly. Routine bodies come from SHOW CREATE on demand.
/// </summary>
public sealed class MySqlMetadata : IMetadataProvider
{
    internal static long Hash(params string?[] parts)
    {
        var h = 14695981039346656037UL;
        foreach (var part in parts)
        {
            foreach (var b in Encoding.UTF8.GetBytes(part ?? "\0null")) { h ^= b; h *= 1099511628211UL; }
            h ^= 0x1F; h *= 1099511628211UL;
        }
        return (long)(h & long.MaxValue);
    }

    internal static long IdOf(string schema, string group, string name) => Hash(schema, group, name);

    const string StampQuery = """
        SELECT t.TABLE_NAME, t.TABLE_TYPE, SHA2(CONCAT_WS('#',
          (SELECT GROUP_CONCAT(CONCAT_WS('|', c.COLUMN_NAME, c.COLUMN_TYPE, c.IS_NULLABLE, IFNULL(c.COLUMN_DEFAULT, '~'), c.EXTRA, IFNULL(c.GENERATION_EXPRESSION, ''))
             ORDER BY c.ORDINAL_POSITION SEPARATOR '\n') FROM information_schema.COLUMNS c WHERE c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME),
          (SELECT GROUP_CONCAT(CONCAT_WS('|', s.INDEX_NAME, s.NON_UNIQUE, s.SEQ_IN_INDEX, IFNULL(s.COLUMN_NAME, '~')) ORDER BY s.INDEX_NAME, s.SEQ_IN_INDEX SEPARATOR '\n')
             FROM information_schema.STATISTICS s WHERE s.TABLE_SCHEMA = t.TABLE_SCHEMA AND s.TABLE_NAME = t.TABLE_NAME),
          (SELECT GROUP_CONCAT(CONCAT_WS('|', k.CONSTRAINT_NAME, k.COLUMN_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME)
             ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION SEPARATOR '\n') FROM information_schema.KEY_COLUMN_USAGE k
             WHERE k.TABLE_SCHEMA = t.TABLE_SCHEMA AND k.TABLE_NAME = t.TABLE_NAME AND k.REFERENCED_TABLE_NAME IS NOT NULL),
          (SELECT GROUP_CONCAT(tr.TRIGGER_NAME ORDER BY tr.TRIGGER_NAME) FROM information_schema.TRIGGERS tr WHERE tr.EVENT_OBJECT_SCHEMA = t.TABLE_SCHEMA AND tr.EVENT_OBJECT_TABLE = t.TABLE_NAME),
          (SELECT v.VIEW_DEFINITION FROM information_schema.VIEWS v WHERE v.TABLE_SCHEMA = t.TABLE_SCHEMA AND v.TABLE_NAME = t.TABLE_NAME)), 256) AS marker, NULL AS parent, NULL AS rtype
        FROM information_schema.TABLES t WHERE t.TABLE_SCHEMA = DATABASE()
        UNION ALL
        SELECT r.ROUTINE_NAME, r.ROUTINE_TYPE, SHA2(CONCAT_WS('#', IFNULL(r.ROUTINE_DEFINITION, ''), IFNULL(r.DTD_IDENTIFIER, ''), r.SQL_MODE,
          (SELECT GROUP_CONCAT(CONCAT_WS('|', p.PARAMETER_MODE, p.PARAMETER_NAME, p.DTD_IDENTIFIER) ORDER BY p.ORDINAL_POSITION SEPARATOR '\n')
             FROM information_schema.PARAMETERS p WHERE p.SPECIFIC_SCHEMA = r.ROUTINE_SCHEMA AND p.SPECIFIC_NAME = r.SPECIFIC_NAME AND p.ORDINAL_POSITION > 0)), 256),
          NULL, r.DTD_IDENTIFIER
        FROM information_schema.ROUTINES r WHERE r.ROUTINE_SCHEMA = DATABASE()
        UNION ALL
        SELECT tr.TRIGGER_NAME, 'TRIGGER', SHA2(CONCAT_WS('#', tr.ACTION_TIMING, tr.EVENT_MANIPULATION, tr.ACTION_STATEMENT, tr.SQL_MODE), 256), tr.EVENT_OBJECT_TABLE, NULL
        FROM information_schema.TRIGGERS tr WHERE tr.TRIGGER_SCHEMA = DATABASE()
        """;

    static (ObjectKind Kind, string Group)? KindOf(string type) => type switch
    {
        "BASE TABLE" or "SYSTEM VERSIONED" => (ObjectKind.Table, "rel"),
        "VIEW" => (ObjectKind.View, "rel"),
        "PROCEDURE" => (ObjectKind.Proc, "proc"),
        "FUNCTION" => (ObjectKind.Func, "func"),
        "TRIGGER" => (ObjectKind.Trigger, "trigger"),
        _ => null, // SEQUENCE (MariaDB), temporary tables: not listed yet
    };

    static async Task<string> SchemaAsync(DbConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DATABASE()";
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? throw new InvalidOperationException("No database is selected for this connection.");
    }

    public async Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection connection, CancellationToken ct)
    {
        var schema = await SchemaAsync(connection, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = StampQuery;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ObjectStamp>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (KindOf(r.GetString(1)) is not { } k) continue;
            var name = r.GetString(0);
            var marker = r.IsDBNull(2) ? "" : r.GetString(2);
            long? parent = r.IsDBNull(3) ? null : IdOf(schema, "rel", r.GetString(3));
            list.Add(new ObjectStamp(IdOf(schema, k.Group, name), schema, name, k.Kind, Hash(marker), r.IsDBNull(4) ? null : r.GetString(4), parent));
        }
        return list;
    }

    public async Task<MetadataBundle> GetDetailsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var schema = await SchemaAsync(connection, ct).ConfigureAwait(false);
        var wanted = objectIds.ToHashSet();
        var columns = new List<ColumnRow>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TABLE_NAME, ORDINAL_POSITION, COLUMN_NAME, COLUMN_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE,
                       IS_NULLABLE, EXTRA, COLUMN_DEFAULT, IFNULL(GENERATION_EXPRESSION, '')
                FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, ORDINAL_POSITION
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = IdOf(schema, "rel", r.GetString(0));
                if (!wanted.Contains(id)) continue;
                var extra = r.IsDBNull(8) ? "" : r.GetString(8);
                columns.Add(new ColumnRow(id, Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : (int)Math.Min(int.MaxValue, Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture)),
                    r.IsDBNull(5) ? null : Convert.ToInt32(r.GetValue(5), CultureInfo.InvariantCulture), r.IsDBNull(6) ? null : Convert.ToInt32(r.GetValue(6), CultureInfo.InvariantCulture),
                    Nullable: r.GetString(7) == "YES", Identity: extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                    Computed: r.GetString(10).Length > 0, Default: r.IsDBNull(9) ? null : r.GetString(9)));
            }
        }
        var indexes = new List<IndexRow>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, COLUMN_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<(long Id, string Index, bool Unique, string Column)>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = IdOf(schema, "rel", r.GetString(0));
                if (wanted.Contains(id)) rows.Add((id, r.GetString(1), Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture) == 0, r.IsDBNull(3) ? "(expression)" : r.GetString(3)));
            }
            foreach (var g in rows.GroupBy(x => (x.Id, x.Index)))
                indexes.Add(new IndexRow(g.Key.Id, g.Key.Index, g.First().Unique, g.Key.Index == "PRIMARY", g.Select(x => x.Column).ToArray()));
        }
        var fks = new List<FkRow>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TABLE_NAME, CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
                FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL
                ORDER BY TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<(long Id, string Name, string Col, string RefSchema, string RefTable, string RefCol)>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = IdOf(schema, "rel", r.GetString(0));
                if (wanted.Contains(id)) rows.Add((id, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
            }
            foreach (var g in rows.GroupBy(x => (x.Id, x.Name)))
            {
                var f = g.First();
                if (f.RefSchema != schema) continue; // a reference into another database is outside this snapshot
                fks.Add(new FkRow(g.Key.Name, g.Key.Id, IdOf(schema, "rel", f.RefTable), g.Select(x => new FkColumnPair { Parent = x.Col, Referenced = x.RefCol }).ToArray()));
            }
        }
        var parms = new List<ParamRow>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT SPECIFIC_NAME, ROUTINE_TYPE, ORDINAL_POSITION, PARAMETER_NAME, DTD_IDENTIFIER, PARAMETER_MODE, CHARACTER_MAXIMUM_LENGTH
                FROM information_schema.PARAMETERS WHERE SPECIFIC_SCHEMA = DATABASE() AND ORDINAL_POSITION > 0 ORDER BY SPECIFIC_NAME, ORDINAL_POSITION
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = IdOf(schema, r.GetString(1) == "PROCEDURE" ? "proc" : "func", r.GetString(0));
                if (!wanted.Contains(id)) continue;
                parms.Add(new ParamRow(id, Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture), r.IsDBNull(3) ? "" : r.GetString(3), r.GetString(4),
                    r.IsDBNull(6) ? null : (int)Math.Min(int.MaxValue, Convert.ToInt64(r.GetValue(6), CultureInfo.InvariantCulture)),
                    Output: !r.IsDBNull(5) && r.GetString(5) is "OUT" or "INOUT", HasDefault: false));
            }
        }
        return new MetadataBundle(columns, parms, indexes, fks);
    }

    static string ShowCreateKind(ObjectKind k) => k switch
    {
        ObjectKind.View => "VIEW", ObjectKind.Proc => "PROCEDURE", ObjectKind.Func or ObjectKind.Tvf => "FUNCTION", ObjectKind.Trigger => "TRIGGER", _ => "TABLE",
    };

    /// <summary>SHOW CREATE, on demand only (never per refresh); the statement is column 1 (2 for routines and triggers).</summary>
    internal static async Task<string?> ShowCreateAsync(DbConnection c, ObjectKind kind, string schema, string name, CancellationToken ct)
    {
        var what = ShowCreateKind(kind);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SHOW CREATE {what} {MySqlTokenizer.Quote(schema)}.{MySqlTokenizer.Quote(name)}";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        var col = what is "PROCEDURE" or "FUNCTION" or "TRIGGER" ? 2 : 1;
        return r.IsDBNull(col) ? null : r.GetString(col); // null: the login may not read the body
    }

    public Task<string?> GetDefinitionAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) =>
        ShowCreateAsync(connection, obj.Kind, obj.Schema, obj.Name, ct);

    public async Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var wanted = objectIds.ToHashSet();
        var list = new List<(long, string?)>();
        foreach (var s in await GetStampsAsync(connection, ct).ConfigureAwait(false))
            if (wanted.Contains(s.Id) && s.Kind is not ObjectKind.Table)
                list.Add((s.Id, await ShowCreateAsync(connection, s.Kind, s.Schema, s.Name, ct).ConfigureAwait(false)));
        return list;
    }

    /// <summary>
    /// MySQL records no dependencies (MariaDB neither): each view, routine and trigger definition is scanned with the
    /// reference finder for real table / view / routine references (not words that merely match a name), and a trigger
    /// uses its table. One catalog query; definitions come from information_schema (bodies the login may read).
    /// </summary>
    public async Task<Dependency[]> GetDependenciesAsync(DbConnection connection, ObjectRef obj, CancellationToken ct)
    {
        var schema = await SchemaAsync(connection, ct).ConfigureAwait(false);
        var objects = new List<(string Name, ObjectKind Kind, string Group, string? Body, string? Table)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TABLE_NAME, IF(TABLE_TYPE = 'VIEW', 'V', 'T'), NULL, NULL FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE IN ('BASE TABLE', 'VIEW', 'SYSTEM VERSIONED');
                SELECT TABLE_NAME, VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA = DATABASE();
                SELECT ROUTINE_NAME, ROUTINE_TYPE, ROUTINE_DEFINITION FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = DATABASE();
                SELECT TRIGGER_NAME, EVENT_OBJECT_TABLE, ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE();
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                objects.Add((r.GetString(0), r.GetString(1) == "V" ? ObjectKind.View : ObjectKind.Table, "rel", null, null));
            await r.NextResultAsync(ct).ConfigureAwait(false);
            var views = new Dictionary<string, string?>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) views[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
            for (var i = 0; i < objects.Count; i++)
                if (objects[i].Kind == ObjectKind.View) objects[i] = objects[i] with { Body = views.GetValueOrDefault(objects[i].Name) };
            await r.NextResultAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var proc = r.GetString(1) == "PROCEDURE";
                objects.Add((r.GetString(0), proc ? ObjectKind.Proc : ObjectKind.Func, proc ? "proc" : "func", r.IsDBNull(2) ? null : r.GetString(2), null));
            }
            await r.NextResultAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) objects.Add((r.GetString(0), ObjectKind.Trigger, "trigger", r.IsDBNull(2) ? null : r.GetString(2), r.GetString(1)));
        }
        var finder = new MySqlReferenceFinder();
        var ctx = new RefContext { DefaultSchema = schema, Columns = (_, _) => null, SchemasOf = _ => [schema] };
        static RefKind RefOf(ObjectKind k) => k switch { ObjectKind.View => RefKind.View, ObjectKind.Proc => RefKind.Procedure, ObjectKind.Func => RefKind.Function, _ => RefKind.Table };
        bool Mentions(string body, (string Name, ObjectKind Kind, string Group, string? Body, string? Table) target) =>
            target.Kind != ObjectKind.Trigger && RefText.ContainsWord(body, target.Name) && finder.Find(body, new RefTarget(RefOf(target.Kind), schema, target.Name), ctx).Hits.Count > 0;
        Dependency Dep(DependencyDirection d, (string Name, ObjectKind Kind, string Group, string? Body, string? Table) o) =>
            new() { Direction = d, Object = new ObjectRef { Schema = schema, Name = o.Name, Kind = o.Kind, ObjectId = IdOf(schema, o.Group, o.Name) } };

        var self = objects.FirstOrDefault(o => o.Name == obj.Name && (o.Kind == obj.Kind || o.Kind is ObjectKind.Table or ObjectKind.View && obj.Kind is ObjectKind.Table or ObjectKind.View));
        if (self.Name == null) return [];
        var list = new List<Dependency>();
        if (self.Body is { } own)
            foreach (var o in objects.Where(o => !(o.Name == self.Name && o.Kind == self.Kind)))
                if (Mentions(own, o)) list.Add(Dep(DependencyDirection.Uses, o));
        if (self.Kind == ObjectKind.Trigger && objects.FirstOrDefault(o => o.Group == "rel" && o.Name == self.Table) is { Name: not null } table && list.All(d => d.Object.Name != table.Name))
            list.Add(Dep(DependencyDirection.Uses, table));
        foreach (var o in objects.Where(o => o.Body != null && !(o.Name == self.Name && o.Kind == self.Kind)))
            if (Mentions(o.Body!, self) || o.Kind == ObjectKind.Trigger && o.Table == self.Name && self.Group == "rel")
                list.Add(Dep(DependencyDirection.UsedBy, o));
        return list.ToArray();
    }

    /// <summary>
    /// Modify → Apply re-runs the definition: a view as CREATE OR REPLACE; a routine or trigger (no OR REPLACE on MySQL) as
    /// DROP … IF EXISTS plus the CREATE under a client DELIMITER, since its body holds ';'. Found on tokens.
    /// </summary>
    public string ToEditableScript(string definition, Capabilities caps)
    {
        var code = MySqlTokenizer.Tokenize(definition).Where(t => !t.IsTrivia).ToList();
        if (code.Count < 3 || !code[0].Is("CREATE")) return definition;
        var k = code.FindIndex(t => t.Is("VIEW") || t.Is("PROCEDURE") || t.Is("FUNCTION") || t.Is("TRIGGER"));
        if (k < 0 || k + 1 >= code.Count) return definition;
        if (code[k].Is("VIEW"))
            return code[1].Is("OR") ? definition : definition[..(code[0].Offset + code[0].Length)] + " OR REPLACE" + definition[(code[0].Offset + code[0].Length)..];
        var n = k + 1;
        while (n < code.Count && (code[n].Is("IF") || code[n].Is("NOT") || code[n].Is("EXISTS"))) n++;
        var end = n;
        while (end + 2 < code.Count && code[end + 1].Text == "." && code[end + 2].Kind is MySqlTokenKind.Word or MySqlTokenKind.QuotedIdent) end += 2;
        var name = definition[code[n].Offset..(code[end].Offset + code[end].Length)];
        var body = definition.TrimEnd().TrimEnd(';');
        return $"DROP {code[k].Text.ToUpperInvariant()} IF EXISTS {name};\nDELIMITER $$\n{body}$$\nDELIMITER ;";
    }

    /// <summary>information_schema.TABLES.TABLE_ROWS: an estimate for InnoDB, exact for MyISAM.</summary>
    public async Task<RowCount[]> GetRowCountsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var schema = await SchemaAsync(connection, ct).ConfigureAwait(false);
        var wanted = objectIds.ToHashSet();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME, TABLE_ROWS FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE'";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RowCount>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = IdOf(schema, "rel", r.GetString(0));
            if (wanted.Contains(id)) list.Add(new RowCount { ObjectId = id, Rows = r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture), Estimated = true });
        }
        return list.ToArray();
    }
}

/// <summary>
/// Plans on MySQL / MariaDB (Phase D2, §11). Estimated: EXPLAIN FORMAT=JSON — v1 (MySQL 8.x, MariaDB) and v2 (MySQL 9's
/// default, an operator tree) both read. Actual: EXPLAIN ANALYZE as JSON v2 on MySQL 9, as TREE text on MySQL 8.0.18+, and
/// ANALYZE FORMAT=JSON on MariaDB. Those run the statement, and MySQL cannot undo DDL (implicit commit), non-transactional
/// tables or — inside the user's open transaction — a nested START TRANSACTION, so only SELECT and TABLE statements get an
/// actual plan; anything else is refused with the reason (before anything runs: <see cref="Refusal"/>), and Core still runs
/// the SELECT inside a transaction it rolls back (a savepoint inside the user's). The raw form is one JSON array of
/// {text, plan}, plan being the server's JSON or TREE text as returned (format "mysql-json").
/// </summary>
public sealed class MySqlPlans : IPlanProvider
{
    public const string WriteRefusal = "An actual plan runs the statement, and MySQL / MariaDB cannot always undo it (DDL commits, some tables are not transactional): only SELECT and TABLE statements get one. Use the estimated plan.";

    public string? Refusal(string sql, PlanMode mode) =>
        mode == PlanMode.Actual && MySqlSplitter.Split(sql).Select(st => st.Tokens.Where(t => !t.IsTrivia).ToList()).Any(code => code.Count > 0 && !ReadOnly(code))
            ? WriteRefusal : null;

    public async Task<PlanResult> GetPlanAsync(DbConnection c, string sql, PlanMode mode, CancellationToken ct)
    {
        var statements = MySqlSplitter.Split(sql).Where(st => st.Tokens.Any(t => !t.IsTrivia)).ToList();
        string prefix = "EXPLAIN FORMAT=JSON ";
        if (mode == PlanMode.Actual)
        {
            foreach (var st in statements)
                if (!ReadOnly(st.Tokens.Where(t => !t.IsTrivia).ToList())) throw new NotSupportedException(WriteRefusal);
            prefix = await ActualPrefixAsync(c, ct).ConfigureAwait(false);
        }
        var raws = new List<(string Text, string Plan)>();
        foreach (var st in statements)
        {
            var text = sql[st.Start..st.End];
            await using var cmd = c.CreateCommand();
            cmd.CommandText = prefix + text;
            raws.Add((text, Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? ""));
        }
        var raw = JsonSerializer.Serialize(raws.Select(x => new Dictionary<string, string> { ["text"] = x.Text, ["plan"] = x.Plan }).ToArray());
        return new PlanResult { Plan = Parse(raw), Raw = raw, Format = "mysql-json" };
    }

    /// <summary>SELECT / TABLE (after an optional WITH), without INTO (a variable, OUTFILE or DUMPFILE).</summary>
    public static bool ReadOnly(IReadOnlyList<MySqlToken> code) =>
        code.Count > 0 && MySqlLanguageService.Verb(code) is "SELECT" or "TABLE" && !MySqlLanguageService.HasTopLevel(code, MySqlLanguageService.Verb(code), "INTO");

    static async Task<string> ActualPrefixAsync(DbConnection c, CancellationToken ct)
    {
        if (MySqlProvider.IsMariaDb(c)) return "ANALYZE FORMAT=JSON ";
        if (!SupportsExplainAnalyze(c.ServerVersion)) throw new NotSupportedException("EXPLAIN ANALYZE needs MySQL 8.0.18 or later.");
        if (MajorOf(c.ServerVersion) >= 9)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT @@explain_json_format_version";
            if (Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == "2") return "EXPLAIN ANALYZE FORMAT=JSON ";
        }
        return "EXPLAIN ANALYZE FORMAT=TREE ";
    }

    static int MajorOf(string version) => int.TryParse(version.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;

    /// <summary>MySQL 8.0.18+ (any MariaDB has ANALYZE).</summary>
    public static bool SupportsExplainAnalyze(string version)
    {
        var parts = version.Split('.', '-');
        int At(int i) => i < parts.Length && int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return At(0) > 8 || At(0) == 8 && (At(1) > 0 || At(2) >= 18);
    }

    public PlanTree Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 256 });
        var statements = new List<PlanStatement>();
        foreach (var st in doc.RootElement.EnumerateArray())
        {
            var plan = st.GetProperty("plan").GetString() ?? "";
            var n = 0;
            PlanNode root;
            if (plan.TrimStart().StartsWith('{'))
            {
                using var json = JsonDocument.Parse(plan, new JsonDocumentOptions { MaxDepth = 256 });
                var e = json.RootElement;
                root = e.TryGetProperty("query_plan", out var qp) ? V2(qp, ref n)
                    : Node(e.TryGetProperty("query_block", out var qb) ? qb : e, "query_block", ref n);
            }
            else root = Tree(plan);
            statements.Add(new PlanStatement { Text = st.GetProperty("text").GetString(), Root = root, SubtreeCost = root.SubtreeCost, MissingIndexes = [], Warnings = [] });
        }
        return new PlanTree { Statements = statements.ToArray() };
    }

    // ------------------------------------------------------------------------------------------ JSON v2 (MySQL 9)

    /// <summary>An operator with its inputs; costs are cumulative, actual rows and times are per loop.</summary>
    static PlanNode V2(JsonElement e, ref int n)
    {
        var id = (n++).ToString(CultureInfo.InvariantCulture);
        var children = new List<PlanNode>();
        if (e.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
            foreach (var i in inputs.EnumerateArray()) if (i.ValueKind == JsonValueKind.Object) children.Add(V2(i, ref n));
        var props = Scalars(e, "inputs");
        double? Num(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var subtree = Num("estimated_total_cost") ?? children.Sum(c => c.SubtreeCost);
        var loops = Num("actual_loops");
        return new PlanNode
        {
            Id = id, Operator = props.GetValueOrDefault("operation", "?"), PhysicalOp = props.GetValueOrDefault("access_type", "?"),
            // The table as v1 names it (schema_name stays in the properties).
            ObjectName = props.GetValueOrDefault("table_name") ?? props.GetValueOrDefault("index_name"),
            EstRows = Num("estimated_rows") ?? 0, EstCost = Math.Max(0, subtree - children.Sum(c => c.SubtreeCost)), SubtreeCost = subtree,
            ActualRows = Num("actual_rows") is { } r ? r * (loops ?? 1) : null, ActualExecutions = loops,
            ElapsedMs = Num("actual_last_row_ms") is { } ms ? ms * (loops ?? 1) : null,
            Warnings = [], Properties = props, Children = children.ToArray(),
        };
    }

    static Dictionary<string, string> Scalars(JsonElement e, params string[] skip)
    {
        var props = new Dictionary<string, string>();
        foreach (var p in e.EnumerateObject())
        {
            if (skip.Contains(p.Name)) continue;
            if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) props[p.Name] = p.Value.ToString();
            else if (p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().All(x => x.ValueKind != JsonValueKind.Object))
                props[p.Name] = string.Join(", ", p.Value.EnumerateArray().Select(x => x.ToString()));
        }
        return props;
    }

    // ------------------------------------------------------------------------------------------ TREE text (EXPLAIN ANALYZE on MySQL 8)

    sealed class TreeLine
    {
        public required string Op;
        public double? Cost, Rows, ActualLast, ActualRows, Loops;
        public bool NeverExecuted;
        public readonly List<TreeLine> Children = [];
    }

    /// <summary>"-> op  (cost=… rows=…) (actual time=first..last rows=… loops=…)", nested by indentation; costs are cumulative.</summary>
    public static PlanNode Tree(string text)
    {
        var roots = new List<TreeLine>();
        var stack = new List<(int Indent, TreeLine Line)>();
        foreach (var raw in text.Split('\n'))
        {
            var arrow = raw.IndexOf("-> ", StringComparison.Ordinal);
            if (arrow < 0 || raw[..arrow].Any(ch => ch != ' ')) continue;
            var line = ParseTreeLine(raw[(arrow + 3)..].TrimEnd('\r'));
            while (stack.Count > 0 && stack[^1].Indent >= arrow) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) roots.Add(line); else stack[^1].Line.Children.Add(line);
            stack.Add((arrow, line));
        }
        var n = 0;
        if (roots.Count == 1) return Build(roots[0], ref n);
        var top = new TreeLine { Op = "plan" };
        top.Children.AddRange(roots);
        return Build(top, ref n);
    }

    static TreeLine ParseTreeLine(string body)
    {
        int Cut(string marker) { var i = body.IndexOf(marker, StringComparison.Ordinal); return i < 0 ? int.MaxValue : i; }
        var end = Math.Min(Cut(" (cost="), Math.Min(Cut(" (actual time="), Cut(" (never executed)")));
        var line = new TreeLine { Op = (end == int.MaxValue ? body : body[..end]).Trim() };
        foreach (var (key, value) in Groups(body, "(cost=")) { if (key == "cost") line.Cost = value; else if (key == "rows") line.Rows = value; }
        foreach (var (key, value) in Groups(body, "(actual time="))
        {
            if (key == "time") line.ActualLast = value; else if (key == "rows") line.ActualRows = value; else if (key == "loops") line.Loops = value;
        }
        line.NeverExecuted = body.Contains("(never executed)", StringComparison.Ordinal);
        return line;
    }

    /// <summary>The key=value pairs of the parenthesized group that starts with <paramref name="open"/>; "time=a..b" gives b.</summary>
    static IEnumerable<(string Key, double Value)> Groups(string body, string open)
    {
        var at = body.IndexOf(open, StringComparison.Ordinal);
        if (at < 0) yield break;
        var close = body.IndexOf(')', at);
        var inner = body[(at + 1)..(close < 0 ? body.Length : close)];
        if (inner.StartsWith("actual ", StringComparison.Ordinal)) inner = inner[7..];
        foreach (var pair in inner.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var v = pair[(eq + 1)..];
            var dots = v.IndexOf("..", StringComparison.Ordinal);
            if (dots >= 0) v = v[(dots + 2)..];
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) yield return (pair[..eq], d);
        }
    }

    static PlanNode Build(TreeLine l, ref int n)
    {
        var id = (n++).ToString(CultureInfo.InvariantCulture);
        var children = new List<PlanNode>();
        foreach (var c in l.Children) children.Add(Build(c, ref n));
        var subtree = l.Cost ?? children.Sum(c => c.SubtreeCost);
        var props = new Dictionary<string, string>();
        if (l.NeverExecuted) props["never executed"] = "true";
        return new PlanNode
        {
            Id = id, Operator = l.Op, PhysicalOp = l.Op.Split(':', ' ')[0], EstRows = l.Rows ?? 0,
            EstCost = Math.Max(0, subtree - children.Sum(c => c.SubtreeCost)), SubtreeCost = subtree,
            ActualRows = l.NeverExecuted ? 0 : l.ActualRows is { } r ? r * (l.Loops ?? 1) : null, ActualExecutions = l.NeverExecuted ? 0 : l.Loops,
            ElapsedMs = l.ActualLast is { } ms ? ms * (l.Loops ?? 1) : null,
            Warnings = [], Properties = props, Children = children.ToArray(),
        };
    }

    // ------------------------------------------------------------------------------------------ JSON v1 (MySQL 8.x, MariaDB; ANALYZE adds r_*)

    static readonly string[] Wrappers = ["ordering_operation", "grouping_operation", "duplicates_removal", "windowing", "temporary_table", "materialized_from_subquery", "query_block", "union_result", "read_sorted_file", "filesort"];

    static PlanNode Node(JsonElement e, string label, ref int n)
    {
        var id = (n++).ToString(CultureInfo.InvariantCulture);
        var children = new List<PlanNode>();
        var props = new Dictionary<string, string>();
        string op = label, obj = "";
        double rows = 0, cost = 0;
        double? elapsed = null;
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "table" when p.Value.ValueKind == JsonValueKind.Object:
                        children.Add(Table(p.Value, ref n));
                        break;
                    case "nested_loop" when p.Value.ValueKind == JsonValueKind.Array:
                        var loop = new List<PlanNode>();
                        foreach (var item in p.Value.EnumerateArray())
                            loop.Add(item.TryGetProperty("table", out var tbl) && tbl.ValueKind == JsonValueKind.Object ? Table(tbl, ref n) : Node(item, "step", ref n));
                        children.Add(new PlanNode
                        {
                            Id = (n++).ToString(CultureInfo.InvariantCulture), Operator = "nested loop", PhysicalOp = "nested loop", Warnings = [], Properties = [],
                            SubtreeCost = loop.Sum(c => c.SubtreeCost), Children = loop.ToArray(),
                        });
                        break;
                    case var w when Wrappers.Contains(w) && p.Value.ValueKind == JsonValueKind.Object:
                        children.Add(Node(p.Value, w.Replace('_', ' '), ref n));
                        break;
                    case "query_specifications" or "union_result" when p.Value.ValueKind == JsonValueKind.Array:
                        foreach (var q in p.Value.EnumerateArray()) children.Add(Node(q.TryGetProperty("query_block", out var b) ? b : q, "query block", ref n));
                        break;
                    case "cost_info" when p.Value.TryGetProperty("query_cost", out var qc):
                        cost = ParseNum(qc);
                        break;
                    case "cost" when p.Value.ValueKind == JsonValueKind.Number: // MariaDB 11
                        cost = p.Value.GetDouble();
                        break;
                    case "r_total_time_ms" when p.Value.ValueKind == JsonValueKind.Number:
                        elapsed = p.Value.GetDouble();
                        break;
                    case "select_id":
                        obj = "select #" + p.Value;
                        break;
                    default:
                        if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) props[p.Name] = p.Value.ToString();
                        break;
                }
            }
        }
        return new PlanNode
        {
            Id = id, Operator = op, PhysicalOp = op, ObjectName = obj.Length > 0 ? obj : null, EstRows = rows, EstCost = cost, ElapsedMs = elapsed,
            SubtreeCost = Math.Max(cost, children.Sum(c => c.SubtreeCost)), Warnings = [], Properties = props, Children = children.ToArray(),
        };
    }

    static PlanNode Table(JsonElement t, ref int n)
    {
        var props = Scalars(t);
        var access = props.GetValueOrDefault("access_type", "?");
        var cost = t.TryGetProperty("cost_info", out var ci) && ci.TryGetProperty("read_cost", out var rc) ? ParseNum(rc)
            : t.TryGetProperty("cost", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetDouble() : 0;
        var rows = props.TryGetValue("rows_examined_per_scan", out var r) || props.TryGetValue("rows", out r) ? ParseNum(r) : 0;
        double? Num(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        // MariaDB ANALYZE: r_rows per loop, r_loops, and the time spent reading this table plus checking its rows.
        var loops = Num("r_loops");
        double? elapsed = Num("r_table_time_ms") is { } tt ? tt + (Num("r_other_time_ms") ?? 0) : null;
        var children = new List<PlanNode>();
        if (t.TryGetProperty("materialized_from_subquery", out var sub)) children.Add(Node(sub, "materialized subquery", ref n));
        return new PlanNode
        {
            Id = (n++).ToString(CultureInfo.InvariantCulture), Operator = $"{access} {props.GetValueOrDefault("table_name", "")}".Trim(), PhysicalOp = access,
            ObjectName = props.GetValueOrDefault("table_name"), EstRows = rows, EstCost = cost, SubtreeCost = cost + children.Sum(c => c.SubtreeCost),
            ActualRows = Num("r_rows") is { } rr ? rr * (loops ?? 1) : null, ActualExecutions = loops, ElapsedMs = elapsed,
            Warnings = [], Properties = props, Children = children.ToArray(),
        };
    }

    static double ParseNum(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : ParseNum(e.GetString() ?? "0");
    static double ParseNum(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}

/// <summary>Script as CREATE (SHOW CREATE) / DROP / DROP and CREATE / SELECT … LIMIT / CALL (Phase D2).</summary>
public sealed class MySqlScripter : IObjectScripter
{
    public async Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, CancellationToken ct)
    {
        var name = $"{MySqlTokenizer.Quote(obj.Schema)}.{MySqlTokenizer.Quote(obj.Name)}";
        var what = obj.Kind switch { ObjectKind.View => "VIEW", ObjectKind.Proc => "PROCEDURE", ObjectKind.Func or ObjectKind.Tvf => "FUNCTION", ObjectKind.Trigger => "TRIGGER", _ => "TABLE" };
        async Task<string> Create()
        {
            var def = await MySqlMetadata.ShowCreateAsync(connection, obj.Kind, obj.Schema, obj.Name, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"The login cannot read the definition of {obj.Schema}.{obj.Name}.");
            // A routine or trigger body holds ';': it runs under a client delimiter.
            return what is "PROCEDURE" or "FUNCTION" or "TRIGGER" ? $"DELIMITER $$\n{def}$$\nDELIMITER ;" : def + ";";
        }
        return kind switch
        {
            ScriptKind.Create => await Create().ConfigureAwait(false),
            ScriptKind.Drop => $"DROP {what} {name};",
            ScriptKind.DropCreate => $"DROP {what} IF EXISTS {name};\n{await Create().ConfigureAwait(false)}",
            ScriptKind.SelectTop when what is "TABLE" or "VIEW" => $"SELECT *\nFROM {name}\nLIMIT 100;",
            ScriptKind.Execute when what == "PROCEDURE" => $"CALL {name}();",
            _ => throw new NotSupportedException($"No {kind.ToString().ToUpperInvariant()} script for a MySQL {what.ToLowerInvariant()}."),
        };
    }
}
