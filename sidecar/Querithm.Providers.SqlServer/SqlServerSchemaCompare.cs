using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql;

namespace Querithm.Providers.SqlServer;

/// <summary>
/// Schema compare for SQL Server: the model comes from catalog views in one batch (SMO per object is far
/// slower than metric 21 allows, §16), normalisation runs on the ScriptDom token stream, and the migration is one
/// transaction where every GO batch is followed by a guard that rolls back and turns on NOEXEC after an error.
/// </summary>
public sealed class SqlServerSchemaCompare : ISchemaCompareDialect
{
    static string Q(string s) => "[" + s.Replace("]", "]]") + "]";
    static string N(string schema, string name) => $"{Q(schema)}.{Q(name)}";
    static string Lit(string s) => "N'" + s.Replace("'", "''") + "'";

    public StringComparer Names => StringComparer.OrdinalIgnoreCase;
    public bool TransactionalDdl => true;
    public bool AlterTypeNeedsDependentsDropped => true;
    public bool RenameColumnNeedsDependentsDropped => true;

    // ---------------------------------------------------------------------------------------------- model

    const string ModelSql = """
        SELECT DB_NAME(), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128));
        SELECT name FROM sys.schemas WHERE schema_id = 1 OR schema_id BETWEEN 5 AND 16383;
        SELECT t.object_id, SCHEMA_NAME(t.schema_id), t.name FROM sys.tables t WHERE t.is_ms_shipped = 0 AND t.is_filetable = 0;
        SELECT c.object_id, c.name, ty.name, SCHEMA_NAME(ty.schema_id), ty.is_user_defined, c.max_length, c.precision, c.scale, c.is_nullable,
               CAST(ic.seed_value AS nvarchar(40)), CAST(ic.increment_value AS nvarchar(40)), dc.name, dc.is_system_named, dc.definition,
               cc.definition, cc.is_persisted,
               CASE WHEN c.collation_name <> CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS sysname) THEN c.collation_name END
        FROM sys.columns c JOIN sys.tables tb ON tb.object_id = c.object_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        WHERE tb.is_ms_shipped = 0 AND tb.is_filetable = 0
        ORDER BY c.object_id, c.column_id;
        SELECT kc.parent_object_id, kc.name, kc.type, kc.is_system_named, i.type_desc, col.name, ic.is_descending_key
        FROM sys.key_constraints kc JOIN sys.tables tb ON tb.object_id = kc.parent_object_id
        JOIN sys.indexes i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
        JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
        WHERE tb.is_ms_shipped = 0
        ORDER BY kc.parent_object_id, kc.name, ic.key_ordinal;
        SELECT cc.parent_object_id, cc.name, cc.is_system_named, cc.definition
        FROM sys.check_constraints cc JOIN sys.tables tb ON tb.object_id = cc.parent_object_id
        WHERE tb.is_ms_shipped = 0 ORDER BY cc.parent_object_id, cc.name;
        SELECT fk.parent_object_id, fk.name, fk.is_system_named, SCHEMA_NAME(rt.schema_id), rt.name,
               fk.delete_referential_action_desc, fk.update_referential_action_desc, pc.name, rc.name
        FROM sys.foreign_keys fk JOIN sys.tables tb ON tb.object_id = fk.parent_object_id
        JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE tb.is_ms_shipped = 0
        ORDER BY fk.parent_object_id, fk.name, fkc.constraint_column_id;
        SELECT i.object_id, i.name, i.is_unique, i.type_desc, i.filter_definition, col.name, ic.is_descending_key, ic.is_included_column
        FROM sys.indexes i JOIN sys.tables tb ON tb.object_id = i.object_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
        WHERE tb.is_ms_shipped = 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.type IN (1, 2) AND i.is_hypothetical = 0
        ORDER BY i.object_id, i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
        SELECT o.object_id, SCHEMA_NAME(o.schema_id), o.name, RTRIM(o.type), m.definition, m.is_schema_bound,
               OBJECT_SCHEMA_NAME(tr.parent_id), OBJECT_NAME(tr.parent_id)
        FROM sys.objects o
        LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
        LEFT JOIN sys.triggers tr ON tr.object_id = o.object_id
        WHERE o.is_ms_shipped = 0 AND o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'TR') AND (o.type <> 'TR' OR tr.parent_class = 1);
        SELECT DISTINCT d.referencing_id, COALESCE(OBJECT_SCHEMA_NAME(d.referenced_id), d.referenced_schema_name, OBJECT_SCHEMA_NAME(d.referencing_id)),
               d.referenced_entity_name
        FROM sys.sql_expression_dependencies d
        WHERE d.referenced_server_name IS NULL AND d.referenced_database_name IS NULL AND d.referenced_class = 1;
        SELECT SCHEMA_NAME(s.schema_id), s.name, TYPE_NAME(s.user_type_id), s.precision, s.scale, CAST(s.start_value AS nvarchar(40)),
               CAST(s.increment AS nvarchar(40)), CAST(s.minimum_value AS nvarchar(40)), CAST(s.maximum_value AS nvarchar(40)), s.is_cycling
        FROM sys.sequences s;
        SELECT SCHEMA_NAME(t.schema_id), t.name, bt.name, t.max_length, t.precision, t.scale, t.is_nullable
        FROM sys.types t JOIN sys.types bt ON bt.user_type_id = t.system_type_id
        WHERE t.is_user_defined = 1 AND t.is_table_type = 0 AND t.is_assembly_type = 0;
        SELECT (SELECT COUNT(*) FROM sys.table_types WHERE is_user_defined = 1),
               (SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables tb ON tb.object_id = i.object_id WHERE tb.is_ms_shipped = 0 AND i.type > 2),
               (SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('PC', 'FS', 'FT', 'TA')),
               (SELECT COUNT(*) FROM sys.synonyms);
        """;

    public async Task<SchemaModel> ReadModelAsync(DbConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = ModelSql;
        cmd.CommandTimeout = 300;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        async Task Each(Action row)
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false)) row();
            await r.NextResultAsync(ct).ConfigureAwait(false);
        }
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);

        string db = "", version = "";
        await Each(() => { db = r.GetString(0); version = r.GetString(1); });
        var schemas = new List<string>();
        await Each(() => schemas.Add(r.GetString(0)));
        var tables = new Dictionary<int, (string Schema, string Name)>();
        await Each(() => tables[r.GetInt32(0)] = (r.GetString(1), r.GetString(2)));

        var cols = new Dictionary<int, List<ModelColumn>>();
        await Each(() =>
        {
            var oid = r.GetInt32(0);
            var type = r.GetBoolean(4) ? N(r.GetString(3), r.GetString(2)) : SqlServerMetadata.TypeDisplay(r.GetString(2), r.GetInt16(5), r.GetByte(6), r.GetByte(7));
            var computed = S(14);
            List(cols, oid).Add(new ModelColumn
            {
                Name = r.GetString(1), Type = computed != null ? "" : type, Nullable = r.GetBoolean(8),
                Identity = r.IsDBNull(9) ? null : $"{r.GetString(9)},{r.GetString(10)}",
                DefaultName = S(11), DefaultSystemNamed = !r.IsDBNull(12) && r.GetBoolean(12), Default = S(13),
                Computed = computed, Persisted = !r.IsDBNull(15) && r.GetBoolean(15), Collation = S(16),
            });
        });

        var keys = new Dictionary<int, List<(string Name, string Type, bool System, string Kind, List<string> Cols, List<bool> Desc)>>();
        await Each(() =>
        {
            var list = List(keys, r.GetInt32(0));
            var name = r.GetString(1);
            if (list.Count == 0 || list[^1].Name != name)
                list.Add((name, r.GetString(2).Trim(), r.GetBoolean(3), r.GetString(4).StartsWith("CLUSTERED", StringComparison.Ordinal) ? "CLUSTERED" : "NONCLUSTERED", [], []));
            list[^1].Cols.Add(r.GetString(5));
            list[^1].Desc.Add(r.GetBoolean(6));
        });

        var checks = new Dictionary<int, List<ModelCheck>>();
        await Each(() => List(checks, r.GetInt32(0)).Add(new ModelCheck { Name = r.GetString(1), SystemNamed = r.GetBoolean(2), Expression = r.GetString(3) }));

        var fks = new Dictionary<int, List<(ModelFk Fk, List<string> Cols, List<string> Refs)>>();
        await Each(() =>
        {
            var list = List(fks, r.GetInt32(0));
            var name = r.GetString(1);
            if (list.Count == 0 || list[^1].Fk.Name != name)
                list.Add((new ModelFk
                {
                    Name = name, SystemNamed = r.GetBoolean(2), RefSchema = r.GetString(3), RefTable = r.GetString(4),
                    OnDelete = r.GetString(5).Replace('_', ' '), OnUpdate = r.GetString(6).Replace('_', ' '), Columns = [], RefColumns = [],
                }, [], []));
            list[^1].Cols.Add(r.GetString(7));
            list[^1].Refs.Add(r.GetString(8));
        });

        var indexes = new Dictionary<int, List<(string Name, bool Unique, string Kind, string? Filter, List<string> Cols, List<bool> Desc, List<string> Include)>>();
        await Each(() =>
        {
            var list = List(indexes, r.GetInt32(0));
            var name = r.GetString(1);
            if (list.Count == 0 || list[^1].Name != name)
                list.Add((name, r.GetBoolean(2), r.GetString(3), S(4), [], [], []));
            if (r.GetBoolean(7)) list[^1].Include.Add(r.GetString(5));
            else { list[^1].Cols.Add(r.GetString(5)); list[^1].Desc.Add(r.GetBoolean(6)); }
        });

        var modules = new Dictionary<int, ModelModule>();
        await Each(() =>
        {
            var kind = r.GetString(3) switch { "V" => ModuleKind.View, "P" => ModuleKind.Procedure, "TR" => ModuleKind.Trigger, _ => ModuleKind.Function };
            modules[r.GetInt32(0)] = new ModelModule
            {
                Schema = r.GetString(1), Name = r.GetString(2), Kind = kind, Definition = S(4), SchemaBound = !r.IsDBNull(5) && r.GetBoolean(5),
                Table = r.IsDBNull(7) ? null : r.GetString(7),
            };
        });
        var refs = new Dictionary<int, List<ModelRef>>();
        await Each(() => { if (!r.IsDBNull(1)) List(refs, r.GetInt32(0)).Add(new ModelRef(r.GetString(1), r.GetString(2))); });

        var sequences = new List<ModelSequence>();
        await Each(() => sequences.Add(new ModelSequence
        {
            Schema = r.GetString(0), Name = r.GetString(1), Type = SqlServerMetadata.TypeDisplay(r.GetString(2), 0, r.GetByte(3), r.GetByte(4)),
            Start = r.GetString(5), Increment = r.GetString(6), Min = S(7), Max = S(8), Cycle = r.GetBoolean(9),
        }));
        var types = new List<ModelType>();
        await Each(() => types.Add(new ModelType
        {
            Schema = r.GetString(0), Name = r.GetString(1), Kind = "alias",
            Definition = $"FROM {SqlServerMetadata.TypeDisplay(r.GetString(2), r.GetInt16(3), r.GetByte(4), r.GetByte(5))} {(r.GetBoolean(6) ? "NULL" : "NOT NULL")}",
        }));
        var notCompared = new List<string>();
        await Each(() =>
        {
            void Note(int i, string what) { var n = r.GetInt32(i); if (n > 0) notCompared.Add($"{n} {what}"); }
            Note(0, "table types are not compared");
            Note(1, "XML, spatial and columnstore indexes are not compared");
            Note(2, "CLR modules are not compared");
            Note(3, "synonyms are not compared");
        });

        var modelTables = tables.Select(kv =>
        {
            var tableCols = cols.GetValueOrDefault(kv.Key) ?? [];
            var names = tableCols.Select(x => x.Name).ToArray();
            var k = keys.GetValueOrDefault(kv.Key) ?? [];
            ModelKey Key((string Name, string Type, bool System, string Kind, List<string> Cols, List<bool> Desc) x) =>
                new() { Name = x.Name, SystemNamed = x.System, Kind = x.Kind, Columns = x.Cols.ToArray(), Descending = x.Desc.ToArray() };
            return new ModelTable
            {
                Schema = kv.Value.Schema, Name = kv.Value.Name,
                Columns = tableCols.Select(x => x.Computed == null ? x : x with { Uses = ColumnsIn(x.Computed, names) }).ToArray(),
                PrimaryKey = k.Where(x => x.Type == "PK").Select(Key).FirstOrDefault(),
                Uniques = k.Where(x => x.Type == "UQ").Select(Key).ToArray(),
                Checks = (checks.GetValueOrDefault(kv.Key) ?? []).Select(ch => ch with { Columns = ColumnsIn(ch.Expression, names) }).ToArray(),
                Fks = (fks.GetValueOrDefault(kv.Key) ?? []).Select(f => f.Fk with { Columns = f.Cols.ToArray(), RefColumns = f.Refs.ToArray() }).ToArray(),
                Indexes = (indexes.GetValueOrDefault(kv.Key) ?? []).Select(i => new ModelIndex
                {
                    Name = i.Name, Unique = i.Unique, Kind = i.Kind, Filter = i.Filter, Columns = i.Cols.ToArray(), Descending = i.Desc.ToArray(),
                    Include = i.Include.Count > 0 ? i.Include.ToArray() : null,
                }).ToArray(),
            };
        }).ToArray();

        return new SchemaModel
        {
            Engine = Engine.Mssql, ServerVersion = version, MajorVersion = Major(version), Database = db, CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Schemas = schemas.ToArray(), Tables = modelTables,
            Modules = modules.Select(kv => kv.Value with { References = (refs.GetValueOrDefault(kv.Key) ?? []).ToArray() }).ToArray(),
            Sequences = sequences.ToArray(), Types = types.ToArray(), NotCompared = notCompared.ToArray(),
        };
    }

    static List<T> List<TKey, T>(Dictionary<TKey, List<T>> map, TKey key) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        return list;
    }

    static int Major(string version) => int.TryParse(version.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;

    // ---------------------------------------------------------------------------------------------- tokens

    static IList<TSqlParserToken> Tokens(string sql) => TSqlParsing.NewParser().GetTokenStream(new StringReader(sql), out _);

    static bool Trivia(TSqlParserToken t) =>
        t.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile;

    static string? IdentName(TSqlParserToken t) => t.TokenType switch
    {
        TSqlTokenType.Identifier => t.Text,
        TSqlTokenType.QuotedIdentifier when t.Text.Length >= 2 && t.Text[0] == '[' => t.Text[1..^1].Replace("]]", "]"),
        TSqlTokenType.QuotedIdentifier when t.Text.Length >= 2 => t.Text[1..^1].Replace("\"\"", "\""),
        _ => null,
    };

    /// <summary>Upper-cased token text without trivia; names unquoted, so [qty] = qty; string literals untouched.</summary>
    static List<string> Canonical(string sql)
    {
        var list = new List<string>();
        foreach (var t in Tokens(sql))
        {
            if (Trivia(t)) continue;
            list.Add(t.TokenType switch
            {
                TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral => t.Text,
                _ => (IdentName(t) ?? t.Text).ToUpperInvariant(),
            });
        }
        return list;
    }

    static string[] ColumnsIn(string expression, string[] columns)
    {
        var set = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        return Tokens(expression).Select(IdentName).Where(n => n != null && set.Contains(n)).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string NormalizeExpression(string expression)
    {
        var t = Canonical(expression);
        while (t.Count >= 2 && t[0] == "(" && t[^1] == ")" && Matching(t, 0) == t.Count - 1) t = t[1..^1];
        return string.Join(" ", t);
    }

    static int Matching(List<string> t, int open)
    {
        var depth = 0;
        for (var i = open; i < t.Count; i++)
        {
            if (t[i] == "(") depth++;
            else if (t[i] == ")" && --depth == 0) return i;
        }
        return -1;
    }

    public string NormalizeBody(string definition)
    {
        var t = Canonical(definition);
        // CREATE / CREATE OR ALTER / ALTER → CREATE; PROC → PROCEDURE; trailing semicolons dropped.
        var i = 0;
        if (i < t.Count && t[i] is "CREATE" or "ALTER")
        {
            i++;
            if (i + 1 < t.Count && t[i] == "OR" && t[i + 1] == "ALTER") i += 2;
        }
        var body = new List<string> { "CREATE" };
        for (; i < t.Count; i++) body.Add(t[i] == "PROC" && body.Count == 1 ? "PROCEDURE" : t[i]);
        while (body.Count > 1 && body[^1] == ";") body.RemoveAt(body.Count - 1);
        return string.Join(" ", body);
    }

    public string WithoutHeader(string definition)
    {
        var tokens = Tokens(definition);
        int Next(int from) { for (var j = from; j < tokens.Count; j++) if (!Trivia(tokens[j])) return j; return -1; }
        var i = Next(0);
        if (i < 0 || tokens[i].TokenType is not (TSqlTokenType.Create or TSqlTokenType.Alter)) return definition;
        var j2 = Next(i + 1);
        if (j2 >= 0 && tokens[j2].TokenType == TSqlTokenType.Or && Next(j2 + 1) is var k and >= 0) j2 = Next(k + 1);
        return j2 < 0 ? "" : definition[tokens[j2].Offset..];
    }

    /// <summary>A name that needs no brackets (letters, digits, underscore; not starting with a digit).</summary>
    static bool Regular(string s) => s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    public string RenameSchema(string sql, string from, string to)
    {
        var tokens = Tokens(sql);
        var sb = new StringBuilder(sql.Length + 16);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.TokenType == TSqlTokenType.EndOfFile) continue;
            if (IdentName(t) is { } name && Names.Equals(name, from) && NextIs(tokens, i, TSqlTokenType.Dot) && !PrevIs(tokens, i, TSqlTokenType.Dot))
                sb.Append(t.TokenType == TSqlTokenType.Identifier && Regular(to) ? to : Q(to)); // keep the original quoting
            else sb.Append(t.Text);
        }
        return sb.ToString();
    }

    static bool NextIs(IList<TSqlParserToken> tokens, int i, TSqlTokenType type)
    {
        for (var j = i + 1; j < tokens.Count; j++) if (!Trivia(tokens[j])) return tokens[j].TokenType == type;
        return false;
    }

    static bool PrevIs(IList<TSqlParserToken> tokens, int i, TSqlTokenType type)
    {
        for (var j = i - 1; j >= 0; j--) if (!Trivia(tokens[j])) return tokens[j].TokenType == type;
        return false;
    }

    // ---------------------------------------------------------------------------------------------- DDL

    public string CreateSchema(string schema) => $"CREATE SCHEMA {Q(schema)};";
    public string DropSchema(string schema) => $"DROP SCHEMA {Q(schema)};";
    public string CreateExtension(string name) => $"-- extension {CommentText.Safe(name)}: SQL Server has no extensions";
    public string DropExtension(string name) => CreateExtension(name);
    public string CreateType(ModelType type) => $"CREATE TYPE {N(type.Schema, type.Name)} {type.Definition};";
    public string DropType(ModelType type) => $"DROP TYPE {N(type.Schema, type.Name)};";
    public string? AddEnumValue(ModelType type, string value, string? after) => null;

    public string CreateSequence(ModelSequence s) =>
        $"CREATE SEQUENCE {N(s.Schema, s.Name)} AS {s.Type} START WITH {s.Start} INCREMENT BY {s.Increment}{Bounds(s)};";

    static string Bounds(ModelSequence s) =>
        $"{(s.Min != null ? $" MINVALUE {s.Min}" : " NO MINVALUE")}{(s.Max != null ? $" MAXVALUE {s.Max}" : " NO MAXVALUE")}{(s.Cycle ? " CYCLE" : " NO CYCLE")}";

    public string AlterSequence(ModelSequence from, ModelSequence to) =>
        !string.Equals(from.Type, to.Type, StringComparison.OrdinalIgnoreCase)
            ? $"{DropSequence(from)}\n{CreateSequence(to)}"
            : $"ALTER SEQUENCE {N(to.Schema, to.Name)} INCREMENT BY {to.Increment}{Bounds(to)};";

    public string DropSequence(ModelSequence s) => $"DROP SEQUENCE {N(s.Schema, s.Name)};";

    static string Paren(string expr) => expr.TrimStart().StartsWith('(') ? expr.Trim() : $"({expr.Trim()})";

    string ColumnDef(ModelColumn c)
    {
        if (c.Computed != null) return $"{Q(c.Name)} AS {Paren(c.Computed)}{(c.Persisted ? " PERSISTED" : "")}";
        var sb = new StringBuilder($"{Q(c.Name)} {c.Type}");
        if (c.Collation != null) sb.Append($" COLLATE {c.Collation}");
        if (c.Identity != null) sb.Append($" IDENTITY({c.Identity})");
        sb.Append(c.Nullable ? " NULL" : " NOT NULL");
        if (c.Default != null) sb.Append($"{(c.DefaultSystemNamed || c.DefaultName == null ? "" : $" CONSTRAINT {Q(c.DefaultName)}")} DEFAULT {c.Default}");
        return sb.ToString();
    }

    static string Named(string name, bool systemNamed) => systemNamed ? "" : $"CONSTRAINT {Q(name)} ";

    static string KeyColumns(string[] cols, bool[]? desc) =>
        string.Join(", ", cols.Select((c, i) => $"{Q(c)}{(desc != null && i < desc.Length && desc[i] ? " DESC" : " ASC")}"));

    static string KeyDef(ModelKey k, bool primary) =>
        $"{Named(k.Name, k.SystemNamed)}{(primary ? "PRIMARY KEY" : "UNIQUE")} {k.Kind ?? (primary ? "CLUSTERED" : "NONCLUSTERED")} ({KeyColumns(k.Columns, k.Descending)})";

    public string CreateTable(ModelTable t)
    {
        var parts = t.Columns.Select(ColumnDef).ToList();
        if (t.PrimaryKey is { } pk) parts.Add(KeyDef(pk, true));
        parts.AddRange(t.Uniques.Select(u => KeyDef(u, false)));
        parts.AddRange(t.Checks.Select(c => $"{Named(c.Name, c.SystemNamed)}CHECK {Paren(c.Expression)}"));
        return $"CREATE TABLE {N(t.Schema, t.Name)} (\n    {string.Join(",\n    ", parts)}\n);";
    }

    public string DropTable(ModelTable t) => $"DROP TABLE {N(t.Schema, t.Name)};";
    public string AddColumn(ModelTable t, ModelColumn c) => $"ALTER TABLE {N(t.Schema, t.Name)} ADD {ColumnDef(c)};";
    public string DropColumn(ModelTable t, ModelColumn c) => $"ALTER TABLE {N(t.Schema, t.Name)} DROP COLUMN {Q(c.Name)};";

    public string RenameColumn(ModelTable t, string from, string to) =>
        $"EXEC sp_rename {Lit($"{N(t.Schema, t.Name)}.{Q(from)}")}, {Lit(to)}, N'COLUMN';";

    public string[] AlterColumn(ModelTable t, ModelColumn from, ModelColumn to)
    {
        if (string.Equals(from.Type, to.Type, StringComparison.OrdinalIgnoreCase) && from.Nullable == to.Nullable && Equals(from.Collation, to.Collation)) return [];
        return [$"ALTER TABLE {N(t.Schema, t.Name)} ALTER COLUMN {Q(to.Name)} {to.Type}{(to.Collation != null ? $" COLLATE {to.Collation}" : "")} {(to.Nullable ? "NULL" : "NOT NULL")};"];
    }

    public string? AlterColumnRefusal(ModelColumn from, ModelColumn to) =>
        (from.Identity == null) != (to.Identity == null) || (from.Identity != null && from.Identity != to.Identity)
            ? $"IDENTITY on {to.Name} cannot be added, removed or reseeded with ALTER COLUMN; rebuild the table to change it"
            : null;

    public string? DropDefault(ModelTable t, ModelColumn c) =>
        c.Default != null && c.DefaultName != null ? $"ALTER TABLE {N(t.Schema, t.Name)} DROP CONSTRAINT {Q(c.DefaultName)};" : null;

    public string? AddDefault(ModelTable t, ModelColumn c) =>
        c.Default == null ? null
            : $"ALTER TABLE {N(t.Schema, t.Name)} ADD {(c.DefaultSystemNamed || c.DefaultName == null ? "" : $"CONSTRAINT {Q(c.DefaultName)} ")}DEFAULT {c.Default} FOR {Q(c.Name)};";

    public string AddKey(ModelTable t, ModelKey k, bool primary) => $"ALTER TABLE {N(t.Schema, t.Name)} ADD {KeyDef(k, primary)};";
    public string DropConstraint(ModelTable t, string name) => $"ALTER TABLE {N(t.Schema, t.Name)} DROP CONSTRAINT {Q(name)};";
    public string AddCheck(ModelTable t, ModelCheck c) => $"ALTER TABLE {N(t.Schema, t.Name)} ADD {Named(c.Name, c.SystemNamed)}CHECK {Paren(c.Expression)};";

    public string CreateIndex(ModelTable t, ModelIndex i) =>
        $"CREATE {(i.Unique ? "UNIQUE " : "")}{i.Kind ?? "NONCLUSTERED"} INDEX {Q(i.Name)} ON {N(t.Schema, t.Name)} ({KeyColumns(i.Columns, i.Descending)})" +
        $"{(i.Include is { Length: > 0 } inc ? $" INCLUDE ({string.Join(", ", inc.Select(Q))})" : "")}{(i.Filter != null ? $" WHERE {i.Filter}" : "")};";

    public string DropIndex(ModelTable t, ModelIndex i) => $"DROP INDEX {Q(i.Name)} ON {N(t.Schema, t.Name)};";

    public string AddFk(ModelTable t, ModelFk f) =>
        $"ALTER TABLE {N(t.Schema, t.Name)} ADD {Named(f.Name, f.SystemNamed)}FOREIGN KEY ({string.Join(", ", f.Columns.Select(Q))}) " +
        $"REFERENCES {N(f.RefSchema, f.RefTable)} ({string.Join(", ", f.RefColumns.Select(Q))}) ON DELETE {f.OnDelete} ON UPDATE {f.OnUpdate};";

    /// <summary>CREATE OR ALTER needs SQL Server 2016 SP1; 2016 itself is treated as RTM (drop and create).</summary>
    static bool CreateOrAlter(int major) => major >= 14;

    public string CreateModule(ModelModule m, int targetMajor)
    {
        var def = Qualify(m.Definition ?? throw new InvalidOperationException($"{m.Schema}.{m.Name} has no readable definition."), m.Schema).Trim();
        return Header(def, CreateOrAlter(targetMajor));
    }

    /// <summary>
    /// The header on the token stream: CREATE → CREATE OR ALTER (from 2017), and a definition saved as ALTER becomes
    /// CREATE OR ALTER (or CREATE before 2017, where the module is dropped first), so it also creates the module.
    /// Before 2017 a saved CREATE OR ALTER becomes CREATE.
    /// </summary>
    static string Header(string def, bool orAlter)
    {
        var tokens = Tokens(def);
        int Next(int from) { for (var j = from; j < tokens.Count; j++) if (!Trivia(tokens[j])) return j; return -1; }
        var first = Next(0);
        if (first < 0 || tokens[first].TokenType is not (TSqlTokenType.Create or TSqlTokenType.Alter)) return def;
        var end = first; // last token of the header verb
        var or = Next(first + 1);
        if (tokens[first].TokenType == TSqlTokenType.Create && or >= 0 && tokens[or].TokenType == TSqlTokenType.Or)
        {
            if (orAlter) return def; // already CREATE OR ALTER
            end = Next(or + 1);
            if (end < 0) return def;
        }
        else if (tokens[first].TokenType == TSqlTokenType.Create && !orAlter) return def;
        return def[..tokens[first].Offset] + (orAlter ? "CREATE OR ALTER" : "CREATE") + def[(tokens[end].Offset + tokens[end].Text.Length)..];
    }

    /// <summary>An unqualified name after CREATE VIEW/PROC/FUNCTION/TRIGGER gets its schema, so the module lands where it belongs.</summary>
    static string Qualify(string def, string schema)
    {
        var tokens = Tokens(def);
        var i = 0;
        int Next(int from) { for (var j = from; j < tokens.Count; j++) if (!Trivia(tokens[j])) return j; return -1; }
        i = Next(0);
        if (i < 0 || tokens[i].TokenType is not (TSqlTokenType.Create or TSqlTokenType.Alter)) return def;
        i = Next(i + 1);
        if (i >= 0 && tokens[i].TokenType == TSqlTokenType.Or) { i = Next(i + 1); i = i >= 0 ? Next(i + 1) : -1; }
        if (i < 0 || tokens[i].TokenType is not (TSqlTokenType.View or TSqlTokenType.Procedure or TSqlTokenType.Proc or TSqlTokenType.Function or TSqlTokenType.Trigger)) return def;
        var name = Next(i + 1);
        if (name < 0 || IdentName(tokens[name]) == null || NextIs(tokens, name, TSqlTokenType.Dot)) return def;
        return def[..tokens[name].Offset] + Q(schema) + "." + def[tokens[name].Offset..];
    }

    public bool NeedsDropBeforeCreate(ModelModule? existing, ModelModule wanted, int targetMajor) =>
        existing != null && (existing.Kind != wanted.Kind || !CreateOrAlter(targetMajor));

    public bool DependencyBlocks(ModelModule dependent) => dependent.SchemaBound;

    public string DropModule(ModelModule m) => $"DROP {Keyword(m.Kind)} {N(m.Schema, m.Name)};";

    static string Keyword(ModuleKind k) => k switch
    {
        ModuleKind.View or ModuleKind.MaterializedView => "VIEW", ModuleKind.Procedure => "PROCEDURE", ModuleKind.Trigger => "TRIGGER", _ => "FUNCTION",
    };

    public string? RefreshModule(ModelModule m) =>
        m.Kind == ModuleKind.View && !m.SchemaBound && m.Definition != null ? $"EXEC sp_refreshsqlmodule {Lit(N(m.Schema, m.Name))};" : null;

    public string RenameObject(RefKind kind, string schema, string name, string newName) =>
        kind == RefKind.Type ? $"EXEC sp_rename {Lit(N(schema, name))}, {Lit(newName)}, N'USERDATATYPE';" : $"EXEC sp_rename {Lit(N(schema, name))}, {Lit(newName)};";

    public string RenameIdentifier(string expression, string from, string to)
    {
        var sb = new StringBuilder(expression.Length + 8);
        foreach (var t in Tokens(expression))
        {
            if (t.TokenType == TSqlTokenType.EndOfFile) continue;
            if (IdentName(t) is { } n && Names.Equals(n, from)) sb.Append(t.TokenType == TSqlTokenType.Identifier && Regular(to) ? to : Q(to));
            else sb.Append(t.Text);
        }
        return sb.ToString();
    }

    /// <summary>After every batch: an error, or a transaction that XACT_ABORT already rolled back, stops the rest (NOEXEC).</summary>
    public const string Guard = "IF @@ERROR <> 0 OR @@TRANCOUNT = 0 BEGIN IF @@TRANCOUNT > 0 ROLLBACK; SET NOEXEC ON; END";

    public string Wrap(IReadOnlyList<MigrationStep> steps, string header)
    {
        var sb = new StringBuilder();
        sb.Append(header.TrimEnd()).Append("\n\nSET XACT_ABORT ON;\nGO\nBEGIN TRANSACTION;\nGO\n");
        foreach (var s in steps)
        {
            sb.Append("-- ").Append(CommentText.Safe(s.Comment)).Append('\n');
            sb.Append(s.Sql.TrimEnd()).Append("\nGO\n").Append(Guard).Append("\nGO\n");
        }
        sb.Append("COMMIT TRANSACTION;\nGO\nSET NOEXEC OFF;\nGO\n");
        return sb.ToString();
    }
}
