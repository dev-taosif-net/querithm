using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql;

namespace Querithm.Providers.SqlServer;

/// <summary>
/// Script as CREATE/ALTER/DROP/EXEC (§10). Modules, DROP, EXEC and SELECT come from catalog views
/// (sys.sql_modules); table CREATE uses SMO when available. If SMO is removed (§2 SMO fallback),
/// only <see cref="ScriptTableWithSmo"/> changes — it falls back to the catalog table scripter.
/// </summary>
public sealed class SqlServerScripter : IObjectScripter
{
    static string Q(string s) => "[" + s.Replace("]", "]]") + "]";

    public async Task<string> ScriptAsync(DbConnection c, ObjectRef o, ScriptKind kind, CancellationToken ct)
    {
        var name = $"{Q(o.Schema)}.{Q(o.Name)}";
        var keyword = o.Kind switch
        {
            ObjectKind.Table => "TABLE", ObjectKind.View => "VIEW", ObjectKind.Proc => "PROCEDURE", ObjectKind.Func or ObjectKind.Tvf => "FUNCTION",
            ObjectKind.Sequence => "SEQUENCE", ObjectKind.Synonym => "SYNONYM", ObjectKind.Type => "TYPE", ObjectKind.Trigger => "TRIGGER", _ => "TABLE",
        };
        var meta = new SqlServerMetadata();
        if (kind is ScriptKind.Enable or ScriptKind.Disable)
        {
            if (o.Kind != ObjectKind.Trigger) throw new InvalidOperationException("Only triggers can be scripted as ENABLE / DISABLE.");
            var parent = await TriggerParentAsync(c, o, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException($"Trigger {name} was not found (it may have been dropped).");
            return $"{(kind == ScriptKind.Enable ? "ENABLE" : "DISABLE")} TRIGGER {name} ON {parent};\n";
        }
        if (o.Kind == ObjectKind.Trigger && kind is ScriptKind.Execute or ScriptKind.SelectTop)
            throw new InvalidOperationException("A trigger cannot be executed or selected from.");
        switch (kind)
        {
            case ScriptKind.Drop:
                return $"DROP {keyword} IF EXISTS {name};\n";
            case ScriptKind.SelectTop:
            {
                var cols = await ColumnsAsync(c, o, ct).ConfigureAwait(false);
                var list = cols.Count == 0 ? "*" : string.Join(",\n       ", cols.Select(x => Q(x.Name)));
                return o.Kind == ObjectKind.Tvf
                    ? $"SELECT TOP (1000) *\nFROM {name}(/* parameters */);\n"
                    : $"SELECT TOP (1000) {list}\nFROM {name};\n";
            }
            case ScriptKind.Execute:
                return await ExecScriptAsync(c, o, name, ct).ConfigureAwait(false);
        }

        string create;
        if (o.Kind == ObjectKind.Table)
            create = await ScriptTableWithSmo(c, o, ct).ConfigureAwait(false);
        else
            create = await meta.GetDefinitionAsync(c, o, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"No definition is available for {name}.");
        create = create.TrimEnd();
        return kind switch
        {
            ScriptKind.Create => create + "\nGO\n",
            ScriptKind.Alter when o.Kind == ObjectKind.Table => throw new InvalidOperationException("Tables cannot be scripted as ALTER; use DROP and CREATE."),
            ScriptKind.Alter => meta.ToEditableScript(create, new Capabilities
            {
                CreateOrAlter = false, CompletionTier = "", Trace = new(), Debugger = new(), EstimatedPlan = new(), ActualPlan = new(),
            }) + "\nGO\n",
            ScriptKind.DropCreate => $"DROP {keyword} IF EXISTS {name};\nGO\n{create}\nGO\n",
            _ => create,
        };
    }

    /// <summary>The quoted [schema].[table] a DML trigger belongs to, or null when there is no such trigger.</summary>
    static async Task<string?> TriggerParentAsync(DbConnection c, ObjectRef o, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT OBJECT_SCHEMA_NAME(t.parent_id), OBJECT_NAME(t.parent_id)
            FROM sys.triggers t WHERE t.parent_class = 1 AND t.object_id = COALESCE(@id, OBJECT_ID(@name))
            """;
        cmd.Parameters.Add(new SqlParameter("@id", o.ObjectId is { } id ? (int)id : DBNull.Value) { SqlDbType = System.Data.SqlDbType.Int });
        cmd.Parameters.Add(new SqlParameter("@name", $"{Q(o.Schema)}.{Q(o.Name)}"));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0) ? $"{Q(r.GetString(0))}.{Q(r.GetString(1))}" : null;
    }

    static async Task<List<(string Name, string Type, bool Nullable, bool Identity, string? Default, bool Computed)>> ColumnsAsync(DbConnection c, ObjectRef o, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, dc.definition, c.is_computed, cc.definition
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@name) ORDER BY c.column_id
            """;
        cmd.Parameters.Add(new SqlParameter("@name", $"{Q(o.Schema)}.{Q(o.Name)}"));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<(string, string, bool, bool, string?, bool)>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var computed = r.GetBoolean(8);
            var type = computed ? "AS " + r.GetString(9) : SqlServerMetadata.TypeDisplay(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4));
            list.Add((r.GetString(0), type, r.GetBoolean(5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetString(7), computed));
        }
        return list;
    }

    static async Task<string> ExecScriptAsync(DbConnection c, ObjectRef o, string name, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT p.name, t.name, p.max_length, p.precision, p.scale, p.is_output
            FROM sys.parameters p JOIN sys.types t ON t.user_type_id = p.user_type_id
            WHERE p.object_id = OBJECT_ID(@name) AND p.parameter_id > 0 ORDER BY p.parameter_id
            """;
        cmd.Parameters.Add(new SqlParameter("@name", name));
        var ps = new List<(string Name, string Type, bool Out)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                ps.Add((r.GetString(0), SqlServerMetadata.TypeDisplay(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4)), r.GetBoolean(5)));
        var sb = new StringBuilder();
        foreach (var p in ps.Where(p => p.Out)) sb.Append($"DECLARE {p.Name} {DeclaredType(p.Type)};\n");
        if (o.Kind is ObjectKind.Func or ObjectKind.Tvf) return sb.Append(SelectFunction(name, o.Kind is ObjectKind.Tvf, ps)).ToString();
        // sys.parameters.has_default_value is always 0 for T-SQL procedures, so defaults come from the definition.
        var defaults = ParamDefaults(await new SqlServerMetadata().GetDefinitionAsync(c, o, ct).ConfigureAwait(false));
        sb.Append(ExecProc(name, ps, defaults));
        foreach (var p in ps.Where(p => p.Out)) sb.Append($"SELECT {p.Name} AS {Q(p.Name.TrimStart('@'))};\n");
        return sb.ToString();
    }

    /// <summary>A function call with NULL for each parameter; the parameter's name and type go in a block comment.</summary>
    internal static string SelectFunction(string name, bool tableValued, IReadOnlyList<(string Name, string Type, bool Out)> ps) =>
        $"SELECT {(tableValued ? "* FROM " : "")}{name}({string.Join(", ", ps.Select(p => $"NULL /* {CommentText.Safe($"{p.Name} {p.Type}")} */"))});\n";

    /// <summary>
    /// The EXEC statement, one parameter per line. The separator goes before the line comment so the comment cannot
    /// swallow it; the comment names the type and whether the parameter is required (unknown when
    /// <paramref name="defaults"/> is null: encrypted or unparsable definition).
    /// </summary>
    internal static string ExecProc(string name, IReadOnlyList<(string Name, string Type, bool Out)> ps, IReadOnlyDictionary<string, string?>? defaults)
    {
        if (ps.Count == 0) return $"EXEC {name};\n";
        var sb = new StringBuilder($"EXEC {name}\n");
        for (var i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            var status = defaults == null ? ""
                : defaults.TryGetValue(p.Name, out var def) ? $", optional (default {OneLine(def) ?? "?"})"
                : ", required";
            sb.Append($"    {p.Name} = {(p.Out ? p.Name + " OUTPUT" : "NULL")}{(i < ps.Count - 1 ? "," : ";")} -- {CommentText.Safe(p.Type + status)}\n");
        }
        return sb.ToString();
    }

    /// <summary>A built-in type as TypeDisplay spells it stays as is; a user-defined type's name (any characters) is bracket-quoted.</summary>
    static string DeclaredType(string type) => type.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '(' or ')' or ',') ? type : Q(type);

    /// <summary>A default shown in a line comment: a line break in it (multi-line string literal) would end the comment.</summary>
    static string? OneLine(string? s) => s == null ? null : string.Join(" ", s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Parameter name → default value text for a CREATE/ALTER PROCEDURE definition, from the ScriptDom AST; null when it does not parse.</summary>
    internal static IReadOnlyDictionary<string, string?>? ParamDefaults(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) return null;
        var script = TSqlParsing.NewParser().Parse(new StringReader(definition), out var errors) as TSqlScript;
        if (errors.Count > 0 || script == null) return null;
        var body = script.Batches.SelectMany(b => b.Statements).OfType<ProcedureStatementBody>().FirstOrDefault();
        if (body == null) return null;
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in body.Parameters.Where(p => p.Value != null))
            map[p.VariableName.Value] = p.Value.StartOffset >= 0 && p.Value.StartOffset + p.Value.FragmentLength <= definition.Length
                ? definition.Substring(p.Value.StartOffset, p.Value.FragmentLength)
                : null;
        return map;
    }

    /// <summary>SMO table scripting; the single swap point if SMO is dropped (§2).</summary>
    static async Task<string> ScriptTableWithSmo(DbConnection c, ObjectRef o, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => SmoScript((SqlConnection)c, o), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await CatalogTableScriptAsync(c, o, ct).ConfigureAwait(false);
        }
    }

    static string SmoScript(SqlConnection conn, ObjectRef o)
    {
        var server = new Microsoft.SqlServer.Management.Smo.Server(new Microsoft.SqlServer.Management.Common.ServerConnection(conn));
        var db = server.Databases[string.IsNullOrEmpty(o.Database) ? conn.Database : o.Database];
        var table = db.Tables[o.Name, o.Schema] ?? throw new InvalidOperationException("Table not found");
        var opts = new Microsoft.SqlServer.Management.Smo.ScriptingOptions
        {
            DriAll = true, Indexes = true, Triggers = false, IncludeHeaders = false, SchemaQualify = true, AnsiPadding = false,
        };
        return string.Join("\nGO\n", table.Script(opts).Cast<string>());
    }

    static async Task<string> CatalogTableScriptAsync(DbConnection c, ObjectRef o, CancellationToken ct)
    {
        var cols = await ColumnsAsync(c, o, ct).ConfigureAwait(false);
        var sb = new StringBuilder($"CREATE TABLE {Q(o.Schema)}.{Q(o.Name)} (\n");
        sb.Append(string.Join(",\n", cols.Select(x =>
            x.Computed ? $"    {Q(x.Name)} {x.Type}"
                       : $"    {Q(x.Name)} {x.Type}{(x.Identity ? " IDENTITY(1,1)" : "")}{(x.Nullable ? " NULL" : " NOT NULL")}{(x.Default != null ? " DEFAULT " + x.Default : "")}")));
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT i.name, STUFF((SELECT ', ' + QUOTENAME(col.name) + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                    FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, ''),
                    i.type_desc
                FROM sys.indexes i WHERE i.object_id = OBJECT_ID(@name) AND i.is_primary_key = 1
                """;
            cmd.Parameters.Add(new SqlParameter("@name", $"{Q(o.Schema)}.{Q(o.Name)}"));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
                sb.Append($",\n    CONSTRAINT {Q(r.GetString(0))} PRIMARY KEY {(r.GetString(2).StartsWith("CLUSTERED") ? "CLUSTERED" : "NONCLUSTERED")} ({r.GetString(1)})");
        }
        sb.Append("\n);");
        return sb.ToString();
    }
}
