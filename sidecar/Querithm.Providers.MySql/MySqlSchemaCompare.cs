using System.Data.Common;
using System.Globalization;
using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.MySql;

namespace Querithm.Providers.MySql;

/// <summary>
/// Schema compare for MySQL / MariaDB (Phase D2). A database is one schema (<see cref="DatabaseIsSchema"/>): the model holds
/// its tables (columns, primary key, checks, indexes, foreign keys) from information_schema and its views, routines and
/// triggers from SHOW CREATE, with DEFINER clauses removed (the account differs between servers) and the object name
/// qualified by the database so a database-to-database compare maps it. Every column change is one MODIFY COLUMN with the
/// whole target definition (MODIFY restates the column: AUTO_INCREMENT, default and ON UPDATE included). DDL commits
/// implicitly, so the script is not one transaction and says so. Normalisation is on <see cref="MySqlTokenizer"/> tokens.
/// </summary>
public sealed class MySqlSchemaCompare : ISchemaCompareDialect
{
    static string Q(string s) => MySqlTokenizer.Quote(s);
    static string N(string schema, string name) => $"{Q(schema)}.{Q(name)}";
    static string Lit(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'";

    /// <summary>Exact names: table names are case-sensitive where the server keeps them so (lower_case_table_names = 0).</summary>
    public StringComparer Names => StringComparer.Ordinal;
    public bool TransactionalDdl => false;
    public bool DatabaseIsSchema => true;
    public bool AlterTypeNeedsDependentsDropped => false; // MODIFY rebuilds the indexes on the column itself
    public bool RenameColumnNeedsDependentsDropped => false;

    // ---------------------------------------------------------------------------------------------- model

    public async Task<SchemaModel> ReadModelAsync(DbConnection c, CancellationToken ct)
    {
        var maria = MySqlProvider.IsMariaDb(c);
        var parts = c.ServerVersion.Split('.', '-');
        int At(int i) => i < parts.Length && int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (maria ? At(0) < 10 || At(0) == 10 && At(1) < 5 : At(0) < 8 || At(0) == 8 && At(1) == 0 && At(2) < 16)
            throw new NotSupportedException(maria ? "Schema compare needs MariaDB 10.5 or later (RENAME COLUMN, CHECK constraints)." : "Schema compare needs MySQL 8.0.16 or later (CHECK constraints).");
        string db;
        var tables = new Dictionary<string, (string Collation, List<ModelColumn> Columns, List<(string Index, bool Unique, int Seq, string? Column, int? Sub, bool Desc, string Type, string? Expr)> Idx,
            List<ModelCheck> Checks, Dictionary<string, (List<string> Cols, string RefSchema, string RefTable, List<string> RefCols, string Del, string Upd)> Fks)>(StringComparer.Ordinal);
        var views = new List<(string Name, string Definition, string CheckOption, string Security)>();
        var routines = new List<(string Name, string Type)>();
        var triggers = new List<(string Name, string Table)>();
        var notCompared = new List<string>();
        await using (var cmd = c.CreateCommand())
        {
            var checks = maria
                ? "SELECT cc.TABLE_NAME, cc.CONSTRAINT_NAME, cc.CHECK_CLAUSE, 'YES' FROM information_schema.CHECK_CONSTRAINTS cc WHERE cc.CONSTRAINT_SCHEMA = DATABASE() ORDER BY 1, 2"
                : "SELECT tc.TABLE_NAME, tc.CONSTRAINT_NAME, cc.CHECK_CLAUSE, tc.ENFORCED FROM information_schema.TABLE_CONSTRAINTS tc JOIN information_schema.CHECK_CONSTRAINTS cc ON cc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA AND cc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME WHERE tc.TABLE_SCHEMA = DATABASE() AND tc.CONSTRAINT_TYPE = 'CHECK' ORDER BY 1, 2";
            var expression = maria ? "NULL" : "s.EXPRESSION";
            cmd.CommandText = $"""
                SELECT DATABASE();
                SELECT TABLE_NAME, TABLE_COLLATION FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE IN ('BASE TABLE', 'SYSTEM VERSIONED') ORDER BY TABLE_NAME;
                SELECT c.TABLE_NAME, c.COLUMN_NAME, c.COLUMN_TYPE, c.IS_NULLABLE, c.COLUMN_DEFAULT, c.EXTRA, IFNULL(c.GENERATION_EXPRESSION, ''), c.COLLATION_NAME, c.DATA_TYPE
                FROM information_schema.COLUMNS c JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
                WHERE c.TABLE_SCHEMA = DATABASE() AND t.TABLE_TYPE IN ('BASE TABLE', 'SYSTEM VERSIONED') ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION;
                SELECT s.TABLE_NAME, s.INDEX_NAME, s.NON_UNIQUE, s.SEQ_IN_INDEX, s.COLUMN_NAME, s.SUB_PART, s.COLLATION, s.INDEX_TYPE, {expression}
                FROM information_schema.STATISTICS s WHERE s.TABLE_SCHEMA = DATABASE() ORDER BY s.TABLE_NAME, s.INDEX_NAME, s.SEQ_IN_INDEX;
                {checks};
                SELECT k.TABLE_NAME, k.CONSTRAINT_NAME, k.COLUMN_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME, r.DELETE_RULE, r.UPDATE_RULE
                FROM information_schema.KEY_COLUMN_USAGE k JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND r.TABLE_NAME = k.TABLE_NAME
                WHERE k.TABLE_SCHEMA = DATABASE() AND k.REFERENCED_TABLE_NAME IS NOT NULL ORDER BY k.TABLE_NAME, k.CONSTRAINT_NAME, k.ORDINAL_POSITION;
                SELECT TABLE_NAME, VIEW_DEFINITION, CHECK_OPTION, SECURITY_TYPE FROM information_schema.VIEWS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME;
                SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = DATABASE() ORDER BY ROUTINE_NAME;
                SELECT TRIGGER_NAME, EVENT_OBJECT_TABLE FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE() ORDER BY TRIGGER_NAME;
                SELECT (SELECT COUNT(*) FROM information_schema.EVENTS WHERE EVENT_SCHEMA = DATABASE()),
                       (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'SEQUENCE'),
                       (SELECT COUNT(*) FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA = DATABASE() AND PARTITION_NAME IS NOT NULL);
                """;
            cmd.CommandTimeout = 300;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            string? S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
            async Task Each(Action row)
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false)) row();
                await r.NextResultAsync(ct).ConfigureAwait(false);
            }
            db = "";
            await Each(() => db = S(0) ?? throw new InvalidOperationException("No database is selected for this connection."));
            await Each(() => tables[r.GetString(0)] = (S(1) ?? "", [], [], [], new(StringComparer.Ordinal)));
            await Each(() =>
            {
                if (!tables.TryGetValue(r.GetString(0), out var t)) return;
                var extra = S(5) ?? "";
                var generation = S(6) ?? "";
                var collation = S(7);
                t.Columns.Add(new ModelColumn
                {
                    Name = r.GetString(1), Type = CanonicalType(r.GetString(2)), Nullable = S(3) == "YES",
                    Default = generation.Length > 0 ? null : DefaultSql(S(4), extra, r.GetString(8), maria),
                    Computed = generation.Length > 0 ? generation : null, Persisted = extra.Contains("STORED", StringComparison.OrdinalIgnoreCase),
                    Identity = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) ? "auto_increment" : null,
                    Collation = collation != null && !string.Equals(collation, t.Collation, StringComparison.OrdinalIgnoreCase) ? collation : null,
                });
            });
            await Each(() =>
            {
                if (!tables.TryGetValue(r.GetString(0), out var t)) return;
                t.Idx.Add((r.GetString(1), Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture) == 0, Convert.ToInt32(r.GetValue(3), CultureInfo.InvariantCulture), S(4),
                    r.IsDBNull(5) ? null : Convert.ToInt32(r.GetValue(5), CultureInfo.InvariantCulture), S(6) == "D", S(7) ?? "BTREE", S(8)));
            });
            await Each(() =>
            {
                if (!tables.TryGetValue(r.GetString(0), out var t)) return;
                var clause = r.GetString(2).Trim();
                var expr = $"CHECK ({clause}){(S(3) == "NO" ? " NOT ENFORCED" : "")}";
                t.Checks.Add(new ModelCheck { Name = r.GetString(1), Expression = expr, Columns = ColumnsIn(clause, t.Columns) });
            });
            await Each(() =>
            {
                if (!tables.TryGetValue(r.GetString(0), out var t)) return;
                var name = r.GetString(1);
                if (!t.Fks.TryGetValue(name, out var f)) t.Fks[name] = f = ([], r.GetString(3), r.GetString(4), [], r.GetString(6), r.GetString(7));
                f.Cols.Add(r.GetString(2));
                f.RefCols.Add(r.GetString(5));
            });
            await Each(() => views.Add((r.GetString(0), S(1) ?? "", S(2) ?? "NONE", S(3) ?? "DEFINER")));
            await Each(() => routines.Add((r.GetString(0), r.GetString(1))));
            await Each(() => triggers.Add((r.GetString(0), r.GetString(1))));
            await Each(() =>
            {
                void Note(int i, string what) { var n = Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); if (n > 0) notCompared.Add($"{n} {what}"); }
                Note(0, "events are not compared");
                Note(1, "sequences are not compared");
                Note(2, "partitions are not compared (tables are compared without PARTITION BY)");
            });
        }
        notCompared.Add("table options (engine, character set, row format) and column comments are not compared");

        var modules = new List<ModelModule>();
        // Views from information_schema.VIEWS: its definition keeps every table qualified by its database, where SHOW CREATE
        // VIEW drops the view's own (and so would read the tables of whatever database the migration runs in).
        foreach (var v in views)
            modules.Add(new ModelModule
            {
                Schema = db, Name = v.Name, Kind = ModuleKind.View,
                Definition = v.Definition.Length == 0 ? null
                    : $"CREATE SQL SECURITY {v.Security} VIEW {N(db, v.Name)} AS {v.Definition}{(v.CheckOption is "NONE" or "" ? "" : $" WITH {v.CheckOption} CHECK OPTION")}",
            });
        foreach (var (name, type) in routines)
        {
            var kind = type == "PROCEDURE" ? ObjectKind.Proc : ObjectKind.Func;
            var def = await CreateTextAsync(c, kind, db, name, ct).ConfigureAwait(false);
            modules.Add(new ModelModule { Schema = db, Name = name, Kind = type == "PROCEDURE" ? ModuleKind.Procedure : ModuleKind.Function, Definition = def });
        }
        foreach (var (name, table) in triggers)
            modules.Add(new ModelModule
            {
                Schema = db, Name = name, Table = table, Kind = ModuleKind.Trigger, Definition = await CreateTextAsync(c, ObjectKind.Trigger, db, name, ct).ConfigureAwait(false),
                References = [new ModelRef(db, table)],
            });
        // MySQL records no dependencies for views and routines: the names a definition mentions that are objects here.
        var known = tables.Keys.Concat(views.Select(v => v.Name)).ToHashSet(StringComparer.Ordinal);
        modules = modules.Select(m => m.Kind == ModuleKind.Trigger || m.Definition == null ? m : m with
        {
            References = NamesIn(m.Definition).Where(n => known.Contains(n) && n != m.Name).Distinct().Select(n => new ModelRef(db, n)).ToArray(),
        }).ToList();

        return new SchemaModel
        {
            Engine = Engine.MySql, ServerVersion = c.ServerVersion, MajorVersion = At(0), Database = db, CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Schemas = [db],
            Tables = tables.Select(kv => Table(db, kv.Key, kv.Value.Columns, kv.Value.Idx, kv.Value.Checks, kv.Value.Fks)).ToArray(),
            Modules = modules.ToArray(), NotCompared = notCompared.ToArray(),
        };
    }

    static ModelTable Table(string db, string name, List<ModelColumn> columns, List<(string Index, bool Unique, int Seq, string? Column, int? Sub, bool Desc, string Type, string? Expr)> idx,
        List<ModelCheck> checks, Dictionary<string, (List<string> Cols, string RefSchema, string RefTable, List<string> RefCols, string Del, string Upd)> fks)
    {
        ModelKey? pk = null;
        var indexes = new List<ModelIndex>();
        foreach (var g in idx.GroupBy(i => i.Index, StringComparer.Ordinal))
        {
            var parts = g.OrderBy(i => i.Seq).ToList();
            if (g.Key == "PRIMARY")
            {
                pk = new ModelKey { Name = "PRIMARY", SystemNamed = true, Columns = parts.Select(p => p.Column ?? "").ToArray() };
                continue;
            }
            var first = parts[0];
            var prefix = first.Type switch { "FULLTEXT" => "FULLTEXT ", "SPATIAL" => "SPATIAL ", _ => first.Unique ? "UNIQUE " : "" };
            var keyParts = string.Join(", ", parts.Select(p =>
                (p.Column == null ? $"({p.Expr})" : Q(p.Column) + (p.Sub is { } sub ? $"({sub})" : "")) + (p.Desc ? " DESC" : "")));
            var usingType = first.Type is "BTREE" or "HASH" ? $" USING {first.Type}" : "";
            indexes.Add(new ModelIndex
            {
                Name = g.Key, Unique = first.Unique, Kind = first.Type, Columns = parts.Where(p => p.Column != null).Select(p => p.Column!).ToArray(),
                Descending = parts.Select(p => p.Desc).ToArray(),
                // ALTER TABLE … ADD <definition>: the name follows INDEX, so the comparer's shape leaves it out.
                Definition = $"{prefix}INDEX {Q(g.Key)} ({keyParts}){usingType}",
            });
        }
        return new ModelTable
        {
            Schema = db, Name = name, Columns = WithUses(columns), PrimaryKey = pk, Checks = checks.ToArray(), Indexes = indexes.ToArray(),
            Fks = fks.Select(kv => new ModelFk
            {
                Name = kv.Key, Columns = kv.Value.Cols.ToArray(), RefSchema = kv.Value.RefSchema, RefTable = kv.Value.RefTable, RefColumns = kv.Value.RefCols.ToArray(),
                OnDelete = kv.Value.Del, OnUpdate = kv.Value.Upd,
            }).ToArray(),
        };
    }

    /// <summary>Integer display widths (MariaDB's int(11), older MySQL) carry no meaning, except tinyint(1) as a boolean.</summary>
    public static string CanonicalType(string columnType)
    {
        var t = MySqlType.Parse(columnType);
        if (t.IntRange != null && !(t.Name == "tinyint" && t.Args is [1]))
        {
            var rest = columnType[(columnType.IndexOf(')') is var close and >= 0 ? close + 1 : t.Name.Length)..].Trim().ToLowerInvariant();
            return (t.Name + (rest.Length > 0 ? " " + rest : "")).Trim();
        }
        return columnType;
    }

    static readonly HashSet<string> Niladic = new(StringComparer.OrdinalIgnoreCase) { "current_timestamp", "now", "localtime", "localtimestamp", "current_date", "curdate", "current_time", "curtime" };
    static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
        { "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "decimal", "numeric", "float", "double", "real", "bit", "year" };

    /// <summary>
    /// COLUMN_DEFAULT as SQL: MariaDB already reports it so (quoted strings, NULL as 'NULL'); MySQL reports a string default
    /// unquoted and an expression default with EXTRA DEFAULT_GENERATED. ON UPDATE comes from EXTRA and stays with the default.
    /// </summary>
    public static string? DefaultSql(string? columnDefault, string extra, string dataType, bool maria)
    {
        string? sql;
        if (maria) sql = columnDefault is null or "NULL" ? null : columnDefault;
        else if (columnDefault == null) sql = null;
        else if (extra.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase))
            sql = IsNiladic(columnDefault) ? columnDefault : $"({columnDefault})";
        else if (NumericTypes.Contains(dataType) && columnDefault.Length > 0 && (char.IsAsciiDigit(columnDefault[0]) || columnDefault[0] is '-' or '.' or 'b' or 'B'))
            sql = columnDefault;
        else sql = Lit(columnDefault);
        var onUpdate = extra.IndexOf("on update ", StringComparison.OrdinalIgnoreCase);
        if (onUpdate >= 0) sql = $"{sql ?? "NULL"} ON UPDATE {extra[(onUpdate + 10)..].Trim()}";
        return sql;
    }

    static bool IsNiladic(string expr)
    {
        var e = expr.Trim();
        var open = e.IndexOf('(');
        return Niladic.Contains(open < 0 ? e : e[..open]);
    }

    static string[] ColumnsIn(string expression, List<ModelColumn> columns)
    {
        var names = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NamesIn(expression).Where(names.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static ModelColumn[] WithUses(List<ModelColumn> cols) =>
        cols.Select(c => c.Computed == null ? c : c with { Uses = ColumnsIn(c.Computed, cols) }).ToArray();

    /// <summary>Identifier names (bare or backticked) in a piece of SQL, on tokens.</summary>
    static IEnumerable<string> NamesIn(string sql) =>
        MySqlTokenizer.Tokenize(sql).Where(t => t.Kind is MySqlTokenKind.Word or MySqlTokenKind.QuotedIdent).Select(MySqlTokenizer.Unquote);

    /// <summary>SHOW CREATE text with the DEFINER clause removed and the object name qualified by the database.</summary>
    static async Task<string?> CreateTextAsync(DbConnection c, ObjectKind kind, string db, string name, CancellationToken ct)
    {
        var def = await MySqlMetadata.ShowCreateAsync(c, kind, db, name, ct).ConfigureAwait(false);
        return def == null ? null : Qualify(StripDefiner(def), db);
    }

    /// <summary>Removes <c>DEFINER = user@host</c> (or CURRENT_USER) from a CREATE header, on tokens.</summary>
    public static string StripDefiner(string def)
    {
        var tokens = MySqlTokenizer.Tokenize(def);
        var code = tokens.Select((t, i) => (t, i)).Where(x => !x.t.IsTrivia).ToList();
        var at = code.FindIndex(x => x.t.Is("DEFINER"));
        if (at < 0 || at + 2 >= code.Count || code[at + 1].t.Text != "=") return def;
        var end = at + 2;
        if (code[end].t.Is("CURRENT_USER"))
        {
            if (end + 2 < code.Count && code[end + 1].t.Text == "(" && code[end + 2].t.Text == ")") end += 2;
        }
        else if (end + 1 < code.Count && code[end + 1].t.Kind == MySqlTokenKind.Variable && code[end + 1].t.Text.StartsWith('@')) end += 1; // `user`@`host`: the host lexes as @`host`
        else if (end + 2 < code.Count && code[end + 1].t.Text == "@") end += 2;
        var start = code[at].t.Offset;
        var stop = code[end].t.Offset + code[end].t.Length;
        while (stop < def.Length && def[stop] == ' ') stop++;
        return def[..start] + def[stop..];
    }

    /// <summary>
    /// The object name after VIEW / PROCEDURE / FUNCTION / TRIGGER as <c>`db`.`name`</c> (SHOW CREATE leaves it bare), and a
    /// trigger's table after ON too: unqualified, CREATE TRIGGER would look for it in whatever database the script runs in.
    /// </summary>
    public static string Qualify(string def, string db)
    {
        var code = MySqlTokenizer.Tokenize(def).Where(t => !t.IsTrivia).ToList();
        var k = code.FindIndex(t => t.Is("VIEW") || t.Is("PROCEDURE") || t.Is("FUNCTION") || t.Is("TRIGGER"));
        if (k < 0 || k + 1 >= code.Count) return def;
        var spans = new List<int>(); // token indices of bare names to qualify, in text order
        if (!(k + 2 < code.Count && code[k + 2].Text == ".")) spans.Add(k + 1);
        if (code[k].Is("TRIGGER"))
        {
            var on = code.FindIndex(k + 1, t => t.Is("ON"));
            if (on > 0 && on + 1 < code.Count && !(on + 2 < code.Count && code[on + 2].Text == ".")) spans.Add(on + 1);
        }
        var sb = new StringBuilder(def);
        foreach (var i in spans.OrderByDescending(i => code[i].Offset))
            sb.Remove(code[i].Offset, code[i].Length).Insert(code[i].Offset, Q(db) + "." + Q(MySqlTokenizer.Unquote(code[i])));
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------- tokens

    static bool PlainName(string s) => s.Length > 0 && !char.IsAsciiDigit(s[0]) && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '$');

    /// <summary>Token text without trivia: words lower-cased, plain backticked names unquoted, niladic time functions without "()".</summary>
    static List<string> Canonical(string sql)
    {
        var list = new List<string>();
        var tokens = MySqlTokenizer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == MySqlTokenKind.Word && Niladic.Contains(t.Text) && i + 2 < tokens.Count && tokens[i + 1].Text == "(" && tokens[i + 2].Text == ")")
            {
                list.Add(t.Text.ToLowerInvariant());
                i += 2;
                continue;
            }
            list.Add(t.Kind switch
            {
                MySqlTokenKind.Word => t.Text.ToLowerInvariant(),
                MySqlTokenKind.QuotedIdent when MySqlTokenizer.Unquote(t) is var inner && PlainName(inner) => inner.ToLowerInvariant(),
                _ => t.Text,
            });
        }
        return list;
    }

    public string NormalizeExpression(string expression)
    {
        var t = Canonical(expression);
        while (t.Count >= 2 && t[0] == "(" && t[^1] == ")" && Matching(t) == t.Count - 1) t = t[1..^1];
        return string.Join(" ", t);
    }

    static int Matching(List<string> t)
    {
        var depth = 0;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i] == "(") depth++;
            else if (t[i] == ")" && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>The header up to VIEW / PROCEDURE / FUNCTION / TRIGGER (CREATE [OR REPLACE] [ALGORITHM=…] [SQL SECURITY …]) goes.</summary>
    public string NormalizeBody(string definition)
    {
        var t = Canonical(StripDefiner(definition));
        var k = t.FindIndex(x => x is "view" or "procedure" or "function" or "trigger");
        var body = new List<string> { "create" };
        body.AddRange(k >= 0 ? t.Skip(k) : t.SkipWhile(x => x is "create" or "or" or "replace"));
        while (body.Count > 1 && body[^1] is ";" or "$$") body.RemoveAt(body.Count - 1);
        return string.Join(" ", body);
    }

    public string WithoutHeader(string definition)
    {
        var code = MySqlTokenizer.Tokenize(definition).Where(t => !t.IsTrivia).ToList();
        var k = code.FindIndex(t => t.Is("VIEW") || t.Is("PROCEDURE") || t.Is("FUNCTION") || t.Is("TRIGGER"));
        return k < 0 ? definition : definition[code[k].Offset..];
    }

    public string RenameSchema(string sql, string from, string to)
    {
        var tokens = MySqlTokenizer.Tokenize(sql);
        var sb = new StringBuilder(sql.Length + 16);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var name = t.Kind is MySqlTokenKind.Word or MySqlTokenKind.QuotedIdent ? MySqlTokenizer.Unquote(t) : null;
            if (name == from && Next(tokens, i) is { } n && n.Text == "." && Prev(tokens, i)?.Text != ".")
                sb.Append(t.Kind == MySqlTokenKind.Word && PlainName(to) ? to : Q(to));
            else sb.Append(t.Text);
        }
        return sb.ToString();
    }

    static MySqlToken? Next(List<MySqlToken> tokens, int i)
    {
        for (var j = i + 1; j < tokens.Count; j++) if (!tokens[j].IsTrivia) return tokens[j];
        return null;
    }

    static MySqlToken? Prev(List<MySqlToken> tokens, int i)
    {
        for (var j = i - 1; j >= 0; j--) if (!tokens[j].IsTrivia) return tokens[j];
        return null;
    }

    // ---------------------------------------------------------------------------------------------- DDL

    static string Unsupported(string what) => throw new NotSupportedException($"MySQL / MariaDB have no {what}.");
    public string CreateSchema(string schema) => $"CREATE DATABASE {Q(schema)};";
    public string DropSchema(string schema) => $"DROP DATABASE {Q(schema)};";
    public string CreateExtension(string name) => Unsupported("extensions");
    public string DropExtension(string name) => Unsupported("extensions");
    public string CreateType(ModelType type) => Unsupported("user-defined types");
    public string DropType(ModelType type) => Unsupported("user-defined types");
    public string? AddEnumValue(ModelType type, string value, string? after) => null;
    public string CreateSequence(ModelSequence s) => Unsupported("sequences in this compare");
    public string AlterSequence(ModelSequence from, ModelSequence to) => Unsupported("sequences in this compare");
    public string DropSequence(ModelSequence s) => Unsupported("sequences in this compare");

    /// <summary>The whole column, as CREATE TABLE and MODIFY COLUMN take it.</summary>
    public static string ColumnDef(ModelColumn c)
    {
        var sb = new StringBuilder($"{Q(c.Name)} {c.Type}");
        if (c.Collation != null) sb.Append($" COLLATE {c.Collation}");
        if (c.Computed != null) sb.Append($" GENERATED ALWAYS AS ({c.Computed}) {(c.Persisted ? "STORED" : "VIRTUAL")}");
        // MariaDB takes no NULL after a generated column (and has no NOT NULL generated ones); MySQL takes both.
        if (c.Computed == null) sb.Append(c.Nullable ? " NULL" : " NOT NULL");
        else if (!c.Nullable) sb.Append(" NOT NULL");
        if (c.Default != null && c.Computed == null) sb.Append($" DEFAULT {c.Default}");
        if (c.Identity != null) sb.Append(" AUTO_INCREMENT");
        return sb.ToString();
    }

    public string CreateTable(ModelTable t)
    {
        var parts = t.Columns.Select(ColumnDef).ToList();
        if (t.PrimaryKey is { } pk) parts.Add($"PRIMARY KEY ({string.Join(", ", pk.Columns.Select(Q))})");
        parts.AddRange(t.Uniques.Select(u => $"CONSTRAINT {Q(u.Name)} UNIQUE ({string.Join(", ", u.Columns.Select(Q))})"));
        parts.AddRange(t.Checks.Select(c => $"CONSTRAINT {Q(c.Name)} {c.Expression}"));
        return $"CREATE TABLE {N(t.Schema, t.Name)} (\n    {string.Join(",\n    ", parts)}\n);";
    }

    public string DropTable(ModelTable t) => $"DROP TABLE {N(t.Schema, t.Name)};";
    public string AddColumn(ModelTable t, ModelColumn c) => $"ALTER TABLE {N(t.Schema, t.Name)} ADD COLUMN {ColumnDef(c)};";
    public string DropColumn(ModelTable t, ModelColumn c) => $"ALTER TABLE {N(t.Schema, t.Name)} DROP COLUMN {Q(c.Name)};";
    public string RenameColumn(ModelTable t, string from, string to) => $"ALTER TABLE {N(t.Schema, t.Name)} RENAME COLUMN {Q(from)} TO {Q(to)};";

    const string ModifyNote = "-- MODIFY restates the whole column; a COMMENT on it is not kept.\n";

    public string[] AlterColumn(ModelTable t, ModelColumn from, ModelColumn to)
    {
        var same = string.Equals(from.Type, to.Type, StringComparison.OrdinalIgnoreCase) && from.Nullable == to.Nullable && from.Identity == to.Identity
                   && string.Equals(from.Collation, to.Collation, StringComparison.OrdinalIgnoreCase);
        return same ? [] : [$"{ModifyNote}ALTER TABLE {N(t.Schema, t.Name)} MODIFY COLUMN {ColumnDef(to)};"];
    }

    public string? AlterColumnRefusal(ModelColumn from, ModelColumn to) => null;

    static bool HasOnUpdate(ModelColumn c) => c.Default?.Contains(" ON UPDATE ", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>DROP DEFAULT leaves ON UPDATE in place: a column with one is restated without both.</summary>
    public string? DropDefault(ModelTable t, ModelColumn c) => c.Default == null ? null : HasOnUpdate(c)
        ? $"{ModifyNote}ALTER TABLE {N(t.Schema, t.Name)} MODIFY COLUMN {ColumnDef(c with { Default = null })};"
        : $"ALTER TABLE {N(t.Schema, t.Name)} ALTER COLUMN {Q(c.Name)} DROP DEFAULT;";

    public string? AddDefault(ModelTable t, ModelColumn c) => c.Default == null ? null : HasOnUpdate(c)
        ? $"{ModifyNote}ALTER TABLE {N(t.Schema, t.Name)} MODIFY COLUMN {ColumnDef(c)};"
        : $"ALTER TABLE {N(t.Schema, t.Name)} ALTER COLUMN {Q(c.Name)} SET DEFAULT {c.Default};";

    public string AddKey(ModelTable t, ModelKey k, bool primary) => primary
        ? $"ALTER TABLE {N(t.Schema, t.Name)} ADD PRIMARY KEY ({string.Join(", ", k.Columns.Select(Q))});"
        : $"ALTER TABLE {N(t.Schema, t.Name)} ADD CONSTRAINT {Q(k.Name)} UNIQUE ({string.Join(", ", k.Columns.Select(Q))});";

    /// <summary>MySQL drops each kind its own way: the table (the one holding the constraint) says which kind this is.</summary>
    public string DropConstraint(ModelTable t, string name)
    {
        var table = N(t.Schema, t.Name);
        if (t.PrimaryKey != null && t.PrimaryKey.Name == name) return $"ALTER TABLE {table} DROP PRIMARY KEY;";
        if (t.Fks.Any(f => f.Name == name)) return $"ALTER TABLE {table} DROP FOREIGN KEY {Q(name)};";
        if (t.Uniques.Any(u => u.Name == name)) return $"ALTER TABLE {table} DROP INDEX {Q(name)};";
        return $"ALTER TABLE {table} DROP CONSTRAINT {Q(name)};"; // checks (MySQL 8.0.19+, MariaDB)
    }

    public string AddCheck(ModelTable t, ModelCheck c) => $"ALTER TABLE {N(t.Schema, t.Name)} ADD CONSTRAINT {Q(c.Name)} {c.Expression};";

    public string CreateIndex(ModelTable t, ModelIndex i) =>
        $"ALTER TABLE {N(t.Schema, t.Name)} ADD {i.Definition ?? throw new InvalidOperationException($"Index {i.Name} has no definition.")};";

    public string DropIndex(ModelTable t, ModelIndex i) => $"ALTER TABLE {N(t.Schema, t.Name)} DROP INDEX {Q(i.Name)};";

    public string AddFk(ModelTable t, ModelFk f) =>
        $"ALTER TABLE {N(t.Schema, t.Name)} ADD CONSTRAINT {Q(f.Name)} FOREIGN KEY ({string.Join(", ", f.Columns.Select(Q))}) " +
        $"REFERENCES {N(f.RefSchema, f.RefTable)} ({string.Join(", ", f.RefColumns.Select(Q))}) ON DELETE {f.OnDelete} ON UPDATE {f.OnUpdate};";

    /// <summary>Views as CREATE OR REPLACE; routine and trigger bodies hold ';', so they run under a client DELIMITER.</summary>
    public string CreateModule(ModelModule m, int targetMajor)
    {
        var def = (m.Definition ?? throw new InvalidOperationException($"{m.Schema}.{m.Name} has no definition.")).Trim().TrimEnd(';');
        if (m.Kind == ModuleKind.View)
        {
            var first = MySqlTokenizer.Tokenize(def).FirstOrDefault(t => !t.IsTrivia);
            var second = MySqlTokenizer.Tokenize(def).Where(t => !t.IsTrivia).Skip(1).FirstOrDefault();
            if (first.Is("CREATE") && !second.Is("OR")) def = def[..(first.Offset + first.Length)] + " OR REPLACE" + def[(first.Offset + first.Length)..];
            return def + ";";
        }
        return $"DELIMITER $$\n{def}$$\nDELIMITER ;";
    }

    /// <summary>MySQL has no CREATE OR REPLACE for routines and triggers: those are dropped and created.</summary>
    public bool NeedsDropBeforeCreate(ModelModule? existing, ModelModule wanted, int targetMajor) =>
        existing != null && (existing.Kind != wanted.Kind || wanted.Kind is ModuleKind.Procedure or ModuleKind.Function or ModuleKind.Trigger);

    /// <summary>MySQL records no dependencies: a view on a dropped table stays (invalid) instead of blocking the drop.</summary>
    public bool DependencyBlocks(ModelModule dependent) => false;

    public string DropModule(ModelModule m) => m.Kind switch
    {
        ModuleKind.View => $"DROP VIEW {N(m.Schema, m.Name)};",
        ModuleKind.Procedure => $"DROP PROCEDURE {N(m.Schema, m.Name)};",
        ModuleKind.Trigger => $"DROP TRIGGER {N(m.Schema, m.Name)};",
        _ => $"DROP FUNCTION {N(m.Schema, m.Name)};",
    };

    public string? RefreshModule(ModelModule m) => null;

    public string RenameObject(RefKind kind, string schema, string name, string newName) => kind switch
    {
        RefKind.Table or RefKind.View => $"RENAME TABLE {N(schema, name)} TO {N(schema, newName)};",
        _ => throw new NotSupportedException("MySQL / MariaDB cannot rename a routine: create it under the new name and drop the old one."),
    };

    public string RenameIdentifier(string expression, string from, string to)
    {
        var sb = new StringBuilder(expression.Length + 8);
        foreach (var t in MySqlTokenizer.Tokenize(expression))
        {
            // Column names are case-insensitive in MySQL / MariaDB.
            var name = t.Kind is MySqlTokenKind.Word or MySqlTokenKind.QuotedIdent ? MySqlTokenizer.Unquote(t) : null;
            sb.Append(name != null && string.Equals(name, from, StringComparison.OrdinalIgnoreCase) ? Q(to) : t.Text);
        }
        return sb.ToString();
    }

    public string Wrap(IReadOnlyList<MigrationStep> steps, string header)
    {
        var sb = new StringBuilder(header.TrimEnd()).Append("\n\n");
        sb.Append("-- MySQL / MariaDB commit every DDL statement on its own: this script is not one transaction. If a step fails,\n");
        sb.Append("-- the steps before it stay applied; read it first and keep a backup.\n\n");
        foreach (var s in steps)
            sb.Append("-- ").Append(CommentText.Safe(s.Comment)).Append('\n').Append(s.Sql.TrimEnd()).Append("\n\n");
        return sb.ToString();
    }
}
