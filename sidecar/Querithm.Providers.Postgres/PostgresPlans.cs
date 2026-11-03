using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;

namespace Querithm.Providers.Postgres;

/// <summary>
/// EXPLAIN (FORMAT JSON) → PlanNode (§11). Core runs ANALYZE inside a transaction it always rolls back — a savepoint
/// inside the user's open transaction, so that transaction and the tab's state stay as they were.
/// </summary>
public sealed class PostgresPlans : IPlanProvider
{
    public async Task<PlanResult> GetPlanAsync(DbConnection c, string sql, PlanMode mode, CancellationToken ct)
    {
        var tokens = PgTokenizer.Tokenize(sql);
        var statements = PgTokenizer.Statements(tokens)
            .Select(st => st.Where(t => !t.IsTrivia).ToList())
            .Where(st => st.Count > 0)
            .Select(st => sql[st[0].Offset..(st[^1].Offset + st[^1].Length)])
            .ToList();
        var options = mode == PlanMode.Actual ? "ANALYZE, BUFFERS, VERBOSE, FORMAT JSON" : "VERBOSE, COSTS, FORMAT JSON";
        var raws = new List<string>();
        foreach (var stmt in statements)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"EXPLAIN ({options}) {stmt}";
            raws.Add((string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
        }
        var parsed = raws.Select((r, i) => (Raw: r, Text: statements[i])).SelectMany(x => ParseOne(x.Raw, x.Text)).ToArray();
        return new PlanResult { Plan = new PlanTree { Statements = parsed }, Raw = "[" + string.Join(",", raws.Select(r => r.Trim().TrimStart('[').TrimEnd(']'))) + "]", Format = "json" };
    }

    public PlanTree Parse(string raw) => new() { Statements = ParseOne(raw, null).ToArray() };

    static IEnumerable<PlanStatement> ParseOne(string raw, string? text)
    {
        using var doc = JsonDocument.Parse(raw);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var plan = item.GetProperty("Plan");
            var total = Num(plan, "Total Cost") ?? 0;
            var counter = 0;
            var root = Node(plan, total, ref counter);
            yield return new PlanStatement
            {
                Text = text,
                SubtreeCost = total,
                Root = root,
                MissingIndexes = [],
                Warnings = [],
                ElapsedMs = item.TryGetProperty("Execution Time", out var et) ? et.GetDouble() : null,
            };
        }
    }

    static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static PlanNode Node(JsonElement p, double statementCost, ref int counter)
    {
        var id = (counter++).ToString(CultureInfo.InvariantCulture);
        var children = new List<PlanNode>();
        if (p.TryGetProperty("Plans", out var plans))
            foreach (var child in plans.EnumerateArray()) children.Add(Node(child, statementCost, ref counter));
        var type = p.GetProperty("Node Type").GetString() ?? "Node";
        var join = p.TryGetProperty("Join Type", out var jt) ? jt.GetString() : null;
        var op = join != null && type.Contains("Join") || type == "Nested Loop" ? $"{type} ({join})" : type;
        var subtree = Num(p, "Total Cost") ?? 0;
        var own = Math.Max(0, subtree - children.Sum(c => c.SubtreeCost));
        var loops = Num(p, "Actual Loops") ?? 1;
        double? actualRows = Num(p, "Actual Rows") is { } ar ? ar * loops : null;
        var est = Num(p, "Plan Rows") ?? 0;
        var props = new Dictionary<string, string>();
        foreach (var prop in p.EnumerateObject())
        {
            if (prop.Name == "Plans") continue;
            props[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Array => string.Join(", ", prop.Value.EnumerateArray().Select(x => x.ToString())),
                _ => prop.Value.ToString(),
            };
        }
        var warnings = new List<PlanWarning>();
        if (p.TryGetProperty("Sort Space Type", out var sst) && sst.GetString() == "Disk")
            warnings.Add(new PlanWarning { Kind = "spill", Message = $"Sort spilled to disk ({props.GetValueOrDefault("Sort Space Used")} kB)" });
        if (Num(p, "Hash Batches") is > 1)
            warnings.Add(new PlanWarning { Kind = "spill", Message = $"Hash used {Num(p, "Hash Batches")} batches (spilled to disk)" });
        if (actualRows is { } a && est > 0 && (a / (est * loops) > 10 || (a > 0 && est * loops / a > 10)))
            warnings.Add(new PlanWarning { Kind = "cardinality", Message = $"Actual rows {a:N0} vs estimated {est * loops:N0}" });
        var relation = p.TryGetProperty("Relation Name", out var rn) ? rn.GetString() : null;
        var schema = p.TryGetProperty("Schema", out var sc) ? sc.GetString() : null;
        var index = p.TryGetProperty("Index Name", out var ix) ? ix.GetString() : null;
        var objectName = relation == null ? index : string.Join(".", new[] { schema, relation, index }.Where(s => !string.IsNullOrEmpty(s)));
        var blocks = Num(p, "Shared Hit Blocks") + Num(p, "Shared Read Blocks");
        return new PlanNode
        {
            Id = id,
            Operator = op,
            PhysicalOp = type,
            LogicalOp = join,
            ObjectName = objectName,
            EstRows = est,
            ActualRows = actualRows,
            ActualExecutions = Num(p, "Actual Loops"),
            EstCost = own,
            SubtreeCost = subtree,
            CostPercent = statementCost > 0 ? Math.Round(own / statementCost * 100, 1) : null,
            ElapsedMs = Num(p, "Actual Total Time") is { } t ? t * loops : null,
            LogicalReads = blocks is { } b ? (long)b : null,
            Warnings = warnings.ToArray(),
            Properties = props,
            Children = children.ToArray(),
        };
    }
}

/// <summary>Script as for PostgreSQL from catalog functions (pg_get_functiondef, pg_get_viewdef, columns).</summary>
public sealed class PostgresScripter : IObjectScripter
{
    public async Task<string> ScriptAsync(DbConnection c, ObjectRef o, ScriptKind kind, CancellationToken ct)
    {
        var name = PostgresMetadata.Qualified(o);
        var keyword = o.Kind switch
        {
            ObjectKind.View => "VIEW", ObjectKind.Proc => "PROCEDURE", ObjectKind.Func or ObjectKind.Tvf => "FUNCTION", ObjectKind.Sequence => "SEQUENCE",
            ObjectKind.Type => "TYPE", ObjectKind.Trigger => "TRIGGER", _ => "TABLE",
        };
        var meta = new PostgresMetadata();
        var major = (c as NpgsqlConnection)?.PostgreSqlVersion.Major;
        if (o.Kind == ObjectKind.Trigger) return await ScriptTriggerAsync(c, o, kind, major, ct).ConfigureAwait(false);
        if (kind is ScriptKind.Enable or ScriptKind.Disable) throw new InvalidOperationException("Only triggers can be scripted as ENABLE / DISABLE.");
        switch (kind)
        {
            case ScriptKind.Drop: return $"DROP {keyword} IF EXISTS {name};\n";
            case ScriptKind.SelectTop: return o.Kind == ObjectKind.Tvf ? $"SELECT * FROM {name}(/* arguments */) LIMIT 1000;\n" : $"SELECT * FROM {name} LIMIT 1000;\n";
            case ScriptKind.Execute:
                return await ArgsAsync(c, o, ct).ConfigureAwait(false) is { } args
                    ? CallRoutine(name, o.Kind == ObjectKind.Proc, args)
                    : o.Kind == ObjectKind.Proc ? $"CALL {name}(/* arguments */);\n" : $"SELECT * FROM {name}(/* arguments */);\n";
        }
        string create;
        if (o.Kind == ObjectKind.Table)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, pg_get_expr(ad.adbin, ad.adrelid)
                FROM pg_attribute a JOIN pg_class cl ON cl.oid = a.attrelid JOIN pg_namespace n ON n.oid = cl.relnamespace
                LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
                WHERE n.nspname = @s AND cl.relname = @n AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum
                """;
            var ps = cmd.CreateParameter(); ps.ParameterName = "s"; ps.Value = o.Schema; cmd.Parameters.Add(ps);
            var pn = cmd.CreateParameter(); pn.ParameterName = "n"; pn.Value = o.Name; cmd.Parameters.Add(pn);
            var cols = new List<string>();
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    cols.Add($"    {r.GetString(0)} {r.GetString(1)}{(r.GetBoolean(2) ? " NOT NULL" : "")}{(r.IsDBNull(3) ? "" : " DEFAULT " + r.GetString(3))}");
            create = $"CREATE TABLE {name} (\n{string.Join(",\n", cols)}\n);";
        }
        else create = await meta.GetDefinitionAsync(c, o, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"No definition for {name}.");
        return kind switch
        {
            ScriptKind.DropCreate => $"DROP {keyword} IF EXISTS {name};\n{create}\n",
            ScriptKind.Alter => meta.ToEditableScript(create, Caps(major)) + "\n",
            _ => create + "\n",
        };
    }

    static Capabilities Caps(int? major) =>
        new() { CompletionTier = "", Trace = new(), Debugger = new(), EstimatedPlan = new(), ActualPlan = new(), MajorVersion = major };

    /// <summary>
    /// A trigger lives on its table: DROP / ENABLE / DISABLE name both (quoted by the server), and ALTER is
    /// CREATE OR REPLACE TRIGGER from PostgreSQL 14 (before it, and for constraint triggers, drop and create).
    /// ENABLE / DISABLE of a trigger on a view is refused with the reason.
    /// </summary>
    static async Task<string> ScriptTriggerAsync(DbConnection c, ObjectRef o, ScriptKind kind, int? major, CancellationToken ct)
    {
        if (kind is ScriptKind.Execute or ScriptKind.SelectTop) throw new InvalidOperationException("A trigger cannot be executed or selected from.");
        await using var cmd = (NpgsqlCommand)c.CreateCommand();
        cmd.CommandText = $"""
            SELECT quote_ident(t.tgname), quote_ident(n.nspname) || '.' || quote_ident(cl.relname), pg_get_triggerdef(t.oid, true) || ';', cl.relkind::text
            FROM pg_trigger t JOIN pg_class cl ON cl.oid = t.tgrelid JOIN pg_namespace n ON n.oid = cl.relnamespace
            WHERE NOT t.tgisinternal AND {PostgresMetadata.NotPartitionClone} AND ((@oid::bigint IS NOT NULL AND t.oid = @oid::oid) OR (@oid::bigint IS NULL AND n.nspname = @s AND t.tgname = @n))
            ORDER BY t.oid LIMIT 1
            """;
        cmd.Parameters.AddWithValue("oid", o.ObjectId is { } id ? id : DBNull.Value).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint;
        cmd.Parameters.AddWithValue("s", o.Schema);
        cmd.Parameters.AddWithValue("n", o.Name);
        string name, table, create, relkind;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException($"Trigger {o.Schema}.{o.Name} was not found (it may have been dropped).");
            (name, table, create, relkind) = (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
        }
        // ALTER TABLE … ENABLE | DISABLE TRIGGER accepts tables and foreign tables only; views have no equivalent.
        if (kind is ScriptKind.Enable or ScriptKind.Disable && relkind == "v")
            throw new InvalidOperationException($"PostgreSQL cannot enable or disable a trigger on a view ({table}); drop the trigger or create it again instead.");
        var drop = $"DROP TRIGGER IF EXISTS {name} ON {table};\n";
        // A constraint trigger has no OR REPLACE form, so it is dropped and created on every version.
        var replace = major >= 14 ? new PostgresMetadata().ToEditableScript(create, Caps(major)) : create;
        return kind switch
        {
            ScriptKind.Drop => drop,
            ScriptKind.DropCreate => drop + create + "\n",
            ScriptKind.Enable => $"ALTER TABLE {table} ENABLE TRIGGER {name};\n",
            ScriptKind.Disable => $"ALTER TABLE {table} DISABLE TRIGGER {name};\n",
            ScriptKind.Alter => replace != create ? replace + "\n" : drop + create + "\n",
            _ => create + "\n",
        };
    }

    /// <summary>A routine argument: name already quoted (null when unnamed), type, pg_proc mode (i, o, b, v, t) and whether a default makes it optional.</summary>
    internal readonly record struct PgArg(string? Name, string Type, char Mode, bool Optional);

    /// <summary>The routine's arguments in order (overloads: the oid when known, else the first by name, as for definitions); null when it does not exist.</summary>
    static async Task<List<PgArg>?> ArgsAsync(DbConnection c, ObjectRef o, CancellationToken ct)
    {
        await using var cmd = (NpgsqlCommand)c.CreateCommand();
        // pronargdefaults counts the trailing input arguments (modes i, b, v) that have a default.
        cmd.CommandText = """
            SELECT CASE WHEN a.name <> '' THEN quote_ident(a.name) END, format_type(a.typ, NULL), a.mode::text, p.pronargs, p.pronargdefaults
            FROM pg_proc p
            LEFT JOIN LATERAL unnest(COALESCE(p.proallargtypes, p.proargtypes::oid[]),
                                     COALESCE(p.proargmodes, array_fill('i'::"char", ARRAY[p.pronargs::int])),
                                     p.proargnames) WITH ORDINALITY AS a(typ, mode, name, ord) ON true
            WHERE p.oid = (SELECT p2.oid FROM pg_proc p2 JOIN pg_namespace n ON n.oid = p2.pronamespace
                           WHERE (@oid::bigint IS NOT NULL AND p2.oid = @oid::oid) OR (@oid::bigint IS NULL AND n.nspname = @s AND p2.proname = @n)
                           ORDER BY p2.oid LIMIT 1)
            ORDER BY a.ord
            """;
        cmd.Parameters.AddWithValue("oid", o.ObjectId is { } id ? id : DBNull.Value).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint;
        cmd.Parameters.AddWithValue("s", o.Schema);
        cmd.Parameters.AddWithValue("n", o.Name);
        List<PgArg>? args = null;
        var input = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            args ??= [];
            if (r.IsDBNull(1)) continue; // no arguments: the LEFT JOIN row
            var mode = r.GetString(2)[0];
            var optional = false;
            if (mode is 'i' or 'b' or 'v') optional = input++ >= r.GetInt16(3) - r.GetInt16(4);
            args.Add(new PgArg(r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), mode, optional));
        }
        return args;
    }

    /// <summary>
    /// CALL (procedure) or SELECT * FROM (function), one argument per line. The separator goes before the line comment so
    /// the comment cannot swallow it; the comment names the type and whether the argument is required. Named notation
    /// (`a => NULL`) when every argument passed has a name and none is VARIADIC, else positional with the name in the comment.
    /// Functions take only their input arguments; procedures also take OUT arguments (as NULL, PostgreSQL 14+).
    /// </summary>
    internal static string CallRoutine(string name, bool isProc, IReadOnlyList<PgArg> args)
    {
        var head = isProc ? $"CALL {name}(" : $"SELECT * FROM {name}(";
        var passed = args.Where(a => a.Mode is 'i' or 'b' or 'v' || (isProc && a.Mode == 'o')).ToList();
        if (passed.Count == 0) return head + ");\n";
        var named = passed.All(a => a.Name != null && a.Mode != 'v');
        var sb = new StringBuilder(head + "\n");
        for (var i = 0; i < passed.Count; i++)
        {
            var a = passed[i];
            var status = a.Mode switch { 'o' => "OUT", 'v' => "variadic", _ => a.Optional ? "optional" : "required" };
            var sep = i < passed.Count - 1 ? "," : "";
            sb.Append(named ? $"    {a.Name} => NULL{sep} -- {CommentText.Safe($"{a.Type}, {status}")}\n" : $"    NULL{sep} -- {CommentText.Safe($"{(a.Name != null ? a.Name + " " : "")}{a.Type}, {status}")}\n");
        }
        return sb.Append(");\n").ToString();
    }
}
