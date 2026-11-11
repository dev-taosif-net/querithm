using System.Data.Common;
using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>
/// Schema compare for Oracle (D4 S11). The model holds the user schemas the login can see (ALL_USERS.ORACLE_MAINTAINED =
/// 'N' that own an object, plus the current schema): tables (columns with exact BYTE / CHAR lengths, defaults including
/// ON NULL, identity kind, virtual columns), primary / unique / check / foreign keys from ALL_CONSTRAINTS, indexes that
/// do not enforce a key (DESC and function-based columns from ALL_IND_EXPRESSIONS), views (ALL_VIEWS with their column
/// list), standalone procedures and functions and table triggers (ALL_SOURCE, compared by normalized definition) and
/// sequences, with module references from ALL_DEPENDENCIES. Everything is read with one query per dictionary view.
/// <para>
/// Not compared, and listed with the count: packages, object types, materialized views, synonyms, partitioning, global
/// temporary, external and index-organized tables' organization, schema / database triggers, plus (always) column
/// collations and visibility, comments, grants, storage and tablespaces. Wrapped units compare as "encrypted".
/// </para>
/// <para>
/// Oracle commits every DDL statement on its own, so a migration is one statement per step with no transaction around it
/// (<see cref="TransactionalDdl"/> is false and <see cref="Wrap"/> says so); Core orders the steps for foreign keys
/// (keys dropped first, added last). Names are always written quoted: the dictionary spelling is exact.
/// Normalisation is on <see cref="OracleTokenizer"/> tokens, never on text.
/// </para>
/// </summary>
public sealed class OracleSchemaCompare : ISchemaCompareDialect
{
    static string Q(string s) => OracleIdentifiers.Quote(s);
    static string N(string schema, string name) => $"{Q(schema)}.{Q(name)}";

    public StringComparer Names => StringComparer.Ordinal;
    public bool TransactionalDdl => false;
    /// <summary>Oracle refuses to change the type of a column a function-based index reads (ORA-30556): what is on it goes first and comes back after.</summary>
    public bool AlterTypeNeedsDependentsDropped => true;
    public bool RenameColumnNeedsDependentsDropped => false;

    // ---------------------------------------------------------------------------------------------- model

    const string Users = """
        SELECT username FROM ALL_USERS u WHERE oracle_maintained = 'N' AND (username = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')
          OR EXISTS (SELECT 1 FROM ALL_OBJECTS o WHERE o.owner = u.username))
        """;

    static OracleCommand Command(DbConnection c, string sql)
    {
        var cmd = (OracleCommand)c.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        cmd.FetchSize = OracleProvider.FetchSizeBytes;
        cmd.InitialLONGFetchSize = -1;
        cmd.CommandTimeout = 300;
        return cmd;
    }

    static async Task EachAsync(DbConnection c, string sql, Action<DbDataReader> row, CancellationToken ct)
    {
        await using var cmd = Command(c, sql);
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) row(r);
        }
        catch (OracleException ex) when (OracleMetadata.IsDenied(ex))
        {
            throw new NotSupportedException($"Schema compare reads the ALL_* dictionary views, and this login cannot read one of them (ORA-{ex.Number:00000}): {ex.Message}", ex);
        }
    }

    static string? S(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
    static int? I(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);

    /// <summary>A column type as Oracle reads it back the same way on any database: text lengths always say BYTE or CHAR.</summary>
    internal static string TypeText(string dataType, string? typeOwner, int? dataLength, int? charLength, string? charUsed, int? precision, int? scale)
    {
        var t = dataType.Trim();
        string Len() => charUsed == "C" ? $"{charLength} CHAR" : $"{dataLength} BYTE";
        return t switch
        {
            "NUMBER" when precision == null && scale == null => "NUMBER",
            "NUMBER" when precision == null => $"NUMBER(*,{scale})",
            "NUMBER" => scale is null or 0 ? $"NUMBER({precision})" : $"NUMBER({precision},{scale})",
            "FLOAT" => precision == null ? "FLOAT" : $"FLOAT({precision})",
            "VARCHAR2" or "CHAR" => $"{t}({Len()})",
            "NVARCHAR2" or "NCHAR" => $"{t}({charLength})",
            "RAW" => $"RAW({dataLength})",
            "UROWID" when dataLength is > 0 => $"UROWID({dataLength})",
            _ when typeOwner != null && typeOwner != "SYS" && typeOwner != "PUBLIC" => $"{Q(typeOwner)}.{Q(t)}",
            _ => t,
        };
    }

    /// <summary>A column default as SQL: NULL is no default (DEFAULT NULL is how one is removed); ON NULL is kept in front.</summary>
    internal static string? DefaultSql(string? dataDefault, bool onNull)
    {
        var d = dataDefault?.Trim();
        if (string.IsNullOrEmpty(d)) return null;
        var code = OracleTokenizer.Tokenize(d).Where(t => !t.IsTrivia).ToList();
        if (code.Count == 1 && code[0].Is("NULL")) return null;
        return onNull ? "ON NULL " + d : d;
    }

    /// <summary>A NOT NULL constraint the column's nullability already says: <c>"COL" IS NOT NULL</c>.</summary>
    internal static bool IsNotNullCheck(string condition, IEnumerable<string> columns)
    {
        var code = OracleTokenizer.Tokenize(condition).Where(t => !t.IsTrivia).ToList();
        return code.Count == 4 && code[0].Kind is OracleTokenKind.QuotedIdent or OracleTokenKind.Word && code[1].Is("IS") && code[2].Is("NOT") && code[3].Is("NULL")
               && columns.Contains(OracleIdentifiers.Identity(code[0]), StringComparer.Ordinal);
    }

    /// <summary>The table's column names an expression reads, on tokens.</summary>
    static string[] ColumnsIn(string expression, IEnumerable<string> columns)
    {
        var names = columns.ToHashSet(StringComparer.Ordinal);
        return OracleTokenizer.Tokenize(expression).Where(t => t.Kind is OracleTokenKind.Word or OracleTokenKind.QuotedIdent)
            .Select(OracleIdentifiers.Identity).Where(names.Contains).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>A function-based index column that is only a quoted column (how Oracle keeps a DESC column): that column.</summary>
    internal static string? PlainColumn(string expression)
    {
        var code = OracleTokenizer.Tokenize(expression).Where(t => !t.IsTrivia).ToList();
        return code.Count == 1 && code[0].Kind is OracleTokenKind.QuotedIdent or OracleTokenKind.Word ? OracleIdentifiers.Identity(code[0]) : null;
    }

    sealed class TableRows
    {
        public required string Schema;
        public required string Name;
        public List<ModelColumn> Columns { get; } = [];
        public Dictionary<string, (string Type, bool Generated, string? Condition, List<string> Columns, string? RefOwner, string? RefName, string? Delete, bool Deferrable)> Constraints { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (bool Unique, string Kind, List<(string Column, bool Desc)> Columns)> Indexes { get; } = new(StringComparer.Ordinal);
    }

    public async Task<SchemaModel> ReadModelAsync(DbConnection c, CancellationToken ct)
    {
        var major = OracleProvider.MajorOf(c.ServerVersion) ?? 0;
        var schemas = new List<string>();
        await EachAsync(c, Users + " ORDER BY username", r => schemas.Add(r.GetString(0)), ct).ConfigureAwait(false);
        var inS = $"IN ({Users})";
        var notCompared = new List<string>();
        var tables = new Dictionary<(string, string), TableRows>();
        var skipped = new HashSet<(string, string)>();

        // Tables: ordinary heap and index-organized tables; partitioned ones are compared without their partitioning.
        await EachAsync(c, $"""
            SELECT {OracleMetadata.NoFeedback} t.owner, t.table_name, t.temporary, t.partitioned, t.iot_type,
                   CASE WHEN EXISTS (SELECT 1 FROM ALL_EXTERNAL_TABLES x WHERE x.owner = t.owner AND x.table_name = t.table_name) THEN 1 ELSE 0 END,
                   CASE WHEN EXISTS (SELECT 1 FROM ALL_MVIEWS m WHERE m.owner = t.owner AND m.container_name = t.table_name) THEN 1 ELSE 0 END
            FROM ALL_TABLES t WHERE t.owner {inS} AND t.nested = 'NO' AND t.secondary = 'N' AND (t.iot_type IS NULL OR t.iot_type = 'IOT') AND t.dropped = 'NO'
            ORDER BY t.owner, t.table_name
            """, r =>
            {
                var key = (r.GetString(0), r.GetString(1));
                if (S(r, 6) == "1") { skipped.Add(key); return; } // a materialized view's container: counted with the materialized views
                if (S(r, 2) == "Y") { notCompared.Add($"{key.Item1}.{key.Item2}: a global temporary table is not compared"); skipped.Add(key); return; }
                if (S(r, 5) == "1") { notCompared.Add($"{key.Item1}.{key.Item2}: an external table is not compared"); skipped.Add(key); return; }
                if (S(r, 3) == "YES") notCompared.Add($"{key.Item1}.{key.Item2}: partitioning is not compared (the table is compared without it)");
                if (S(r, 4) == "IOT") notCompared.Add($"{key.Item1}.{key.Item2}: index organization is not compared (a new table is created as an ordinary heap table)");
                tables[key] = new TableRows { Schema = key.Item1, Name = key.Item2 };
            }, ct).ConfigureAwait(false);

        var identity = new Dictionary<(string, string, string), string>();
        await EachAsync(c, $"""
            SELECT {OracleMetadata.NoFeedback} owner, table_name, column_name, generation_type FROM ALL_TAB_IDENTITY_COLS WHERE owner {inS}
            """, r => identity[(r.GetString(0), r.GetString(1), r.GetString(2))] = S(r, 3) ?? "BY DEFAULT", ct).ConfigureAwait(false);

        await EachAsync(c, $"""
            SELECT {OracleMetadata.NoFeedback} owner, table_name, column_name, data_type, data_type_owner, data_length, char_length, char_used, data_precision, data_scale,
                   nullable, data_default_vc, default_on_null, virtual_column, identity_column
            FROM ALL_TAB_COLS WHERE owner {inS} AND user_generated = 'YES'
            ORDER BY owner, table_name, NVL(column_id, 100000 + internal_column_id)
            """, r =>
            {
                if (!tables.TryGetValue((r.GetString(0), r.GetString(1)), out var t)) return;
                var name = r.GetString(2);
                var isIdentity = S(r, 14) == "YES";
                var isVirtual = S(r, 13) == "YES";
                var onNull = S(r, 12) == "YES";
                var kind = isIdentity && identity.TryGetValue((t.Schema, t.Name, name), out var g) ? g + (onNull ? " ON NULL" : "") : null;
                t.Columns.Add(new ModelColumn
                {
                    Name = name,
                    Type = TypeText(S(r, 3) ?? "", S(r, 4), I(r, 5), I(r, 6), S(r, 7), I(r, 8), I(r, 9)),
                    Nullable = S(r, 10) != "N",
                    Default = isIdentity || isVirtual ? null : DefaultSql(S(r, 11), onNull),
                    Computed = isVirtual ? S(r, 11)?.Trim() : null,
                    Identity = kind,
                });
            }, ct).ConfigureAwait(false);

        await EachAsync(c, $"""
            SELECT {OracleMetadata.NoFeedback} k.owner, k.table_name, k.constraint_name, k.constraint_type, k.generated, k.search_condition_vc, cc.column_name,
                   r.owner, r.table_name, k.delete_rule, k.deferrable
            FROM ALL_CONSTRAINTS k
            JOIN ALL_CONS_COLUMNS cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name AND cc.table_name = k.table_name
            LEFT JOIN ALL_CONSTRAINTS r ON r.owner = k.r_owner AND r.constraint_name = k.r_constraint_name
            WHERE k.owner {inS} AND k.constraint_type IN ('P', 'U', 'C', 'R')
            ORDER BY k.owner, k.table_name, k.constraint_name, cc.position
            """, r =>
            {
                if (!tables.TryGetValue((r.GetString(0), r.GetString(1)), out var t)) return;
                var name = r.GetString(2);
                if (!t.Constraints.TryGetValue(name, out var k))
                    t.Constraints[name] = k = (r.GetString(3), S(r, 4) == "GENERATED NAME", S(r, 5), [], S(r, 7), S(r, 8), S(r, 9), S(r, 10) == "DEFERRABLE");
                if (S(r, 6) is { } col) k.Columns.Add(col);
            }, ct).ConfigureAwait(false);

        // FK referenced columns, by constraint, in position order (the referenced key's own columns).
        var refColumns = new Dictionary<(string, string), List<string>>();
        await EachAsync(c, $"""
            SELECT {OracleMetadata.NoFeedback} k.owner, k.constraint_name, rc.column_name
            FROM ALL_CONSTRAINTS k JOIN ALL_CONS_COLUMNS rc ON rc.owner = k.r_owner AND rc.constraint_name = k.r_constraint_name
            WHERE k.owner {inS} AND k.constraint_type = 'R' ORDER BY k.owner, k.constraint_name, rc.position
            """, r =>
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!refColumns.TryGetValue(key, out var list)) refColumns[key] = list = [];
                list.Add(r.GetString(2));
            }, ct).ConfigureAwait(false);

        // Indexes that no constraint enforces (a key's index comes with the key).
        await EachAsync(c, $"""
            SELECT {OracleMetadata.NoFeedback} i.table_owner, i.table_name, i.index_name, i.uniqueness, i.index_type, ic.column_name, ic.descend, e.column_expression, i.partitioned
            FROM ALL_INDEXES i
            JOIN ALL_IND_COLUMNS ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
            LEFT JOIN ALL_IND_EXPRESSIONS e ON e.index_owner = ic.index_owner AND e.index_name = ic.index_name AND e.column_position = ic.column_position
            WHERE i.table_owner {inS} AND i.owner = i.table_owner AND i.generated = 'N' AND i.index_type IN ('NORMAL', 'NORMAL/REV', 'BITMAP', 'FUNCTION-BASED NORMAL', 'FUNCTION-BASED BITMAP')
              AND NOT EXISTS (SELECT 1 FROM ALL_CONSTRAINTS k WHERE k.owner = i.table_owner AND k.table_name = i.table_name AND k.constraint_type IN ('P', 'U')
                              AND k.index_owner = i.owner AND k.index_name = i.index_name)
            ORDER BY i.table_owner, i.table_name, i.index_name, ic.column_position
            """, r =>
            {
                if (!tables.TryGetValue((r.GetString(0), r.GetString(1)), out var t)) return;
                var name = r.GetString(2);
                var type = S(r, 4) ?? "NORMAL";
                if (!t.Indexes.TryGetValue(name, out var ix))
                {
                    t.Indexes[name] = ix = (S(r, 3) == "UNIQUE", type.Contains("BITMAP", StringComparison.Ordinal) ? "BITMAP" : type.EndsWith("/REV", StringComparison.Ordinal) ? "NORMAL REVERSE" : "NORMAL", []);
                    if (S(r, 8) == "YES") notCompared.Add($"{t.Schema}.{name}: index partitioning is not compared");
                }
                var expr = S(r, 7)?.Trim();
                var column = expr == null ? r.GetString(5) : PlainColumn(expr) ?? expr;
                ix.Columns.Add((column, S(r, 6) == "DESC"));
            }, ct).ConfigureAwait(false);

        // Views with their output columns.
        var viewText = new List<(string Owner, string Name, string? Text)>();
        await EachAsync(c, $"SELECT {OracleMetadata.NoFeedback} owner, view_name, text FROM ALL_VIEWS WHERE owner {inS} ORDER BY owner, view_name",
            r => viewText.Add((r.GetString(0), r.GetString(1), S(r, 2))), ct).ConfigureAwait(false);
        var viewCols = new Dictionary<(string, string), List<string>>();
        if (viewText.Count > 0)
            await EachAsync(c, $"""
                SELECT {OracleMetadata.NoFeedback} c.owner, c.table_name, c.column_name FROM ALL_TAB_COLUMNS c
                JOIN ALL_VIEWS v ON v.owner = c.owner AND v.view_name = c.table_name WHERE c.owner {inS} ORDER BY c.owner, c.table_name, c.column_id
                """, r =>
                {
                    var key = (r.GetString(0), r.GetString(1));
                    if (!viewCols.TryGetValue(key, out var list)) viewCols[key] = list = [];
                    list.Add(r.GetString(2));
                }, ct).ConfigureAwait(false);

        // Standalone routines and table triggers from ALL_SOURCE.
        var source = new Dictionary<(string Owner, string Name, string Type), StringBuilder>();
        await EachAsync(c, $"""
            SELECT {OracleMetadata.NoFeedback} owner, name, type, text FROM ALL_SOURCE WHERE owner {inS} AND type IN ('PROCEDURE', 'FUNCTION', 'TRIGGER')
            ORDER BY owner, name, type, line
            """, r =>
            {
                var key = (r.GetString(0), r.GetString(1), r.GetString(2));
                if (!source.TryGetValue(key, out var sb)) source[key] = sb = new StringBuilder();
                sb.Append(S(r, 3));
            }, ct).ConfigureAwait(false);
        var triggers = new Dictionary<(string, string), (string? TableOwner, string? Table, string BaseType)>();
        await EachAsync(c, $"SELECT {OracleMetadata.NoFeedback} owner, trigger_name, table_owner, table_name, base_object_type FROM ALL_TRIGGERS WHERE owner {inS}",
            r => triggers[(r.GetString(0), r.GetString(1))] = (S(r, 2), S(r, 3), (S(r, 4) ?? "").Trim()), ct).ConfigureAwait(false);

        var sequences = new List<ModelSequence>();
        await EachAsync(c, $"""
            SELECT {OracleMetadata.NoFeedback} sequence_owner, sequence_name, TO_CHAR(min_value, 'TM9'), TO_CHAR(max_value, 'TM9'), TO_CHAR(increment_by, 'TM9'), cycle_flag
            FROM ALL_SEQUENCES WHERE sequence_owner {inS} AND sequence_name NOT LIKE 'ISEQ$$\_%' ESCAPE '\' ORDER BY sequence_owner, sequence_name
            """, r =>
            {
                var inc = S(r, 4) ?? "1";
                sequences.Add(new ModelSequence
                {
                    Schema = r.GetString(0), Name = r.GetString(1), Type = "NUMBER", Increment = inc, Min = S(r, 2), Max = S(r, 3), Cycle = S(r, 5) == "Y",
                    Start = (inc.StartsWith('-') ? S(r, 3) : S(r, 2)) ?? "1",
                });
            }, ct).ConfigureAwait(false);

        var references = new Dictionary<(string, string), List<ModelRef>>();
        await EachAsync(c, $"""
            SELECT {OracleMetadata.NoFeedback} owner, name, referenced_owner, referenced_name FROM ALL_DEPENDENCIES
            WHERE owner {inS} AND type IN ('VIEW', 'PROCEDURE', 'FUNCTION', 'TRIGGER') AND referenced_owner {inS}
              AND referenced_type IN ('TABLE', 'VIEW', 'PROCEDURE', 'FUNCTION', 'SEQUENCE')
            """, r =>
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!references.TryGetValue(key, out var list)) references[key] = list = [];
                list.Add(new ModelRef(r.GetString(2), r.GetString(3)));
            }, ct).ConfigureAwait(false);

        await EachAsync(c, $"""
            SELECT {OracleMetadata.NoFeedback} object_type, COUNT(*) FROM ALL_OBJECTS WHERE owner {inS}
              AND object_type IN ('PACKAGE', 'TYPE', 'MATERIALIZED VIEW', 'SYNONYM') GROUP BY object_type ORDER BY object_type
            """, r =>
            {
                var n = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                notCompared.Add(r.GetString(0) switch
                {
                    "PACKAGE" => $"{n} package(s) are not compared (specifications, bodies and their members)",
                    "TYPE" => $"{n} object type(s) are not compared (a type with dependents cannot be replaced)",
                    "MATERIALIZED VIEW" => $"{n} materialized view(s) are not compared",
                    _ => $"{n} synonym(s) are not compared",
                });
            }, ct).ConfigureAwait(false);
        notCompared.Add("column collations and visibility (INVISIBLE), comments, grants, storage, tablespaces and constraint states (DISABLE, NOVALIDATE, INITIALLY DEFERRED) are not compared");

        // ------------------------------------------------------------------ assemble
        var modelTables = new List<ModelTable>();
        foreach (var t in tables.Values)
        {
            var colNames = t.Columns.Select(x => x.Name).ToList();
            ModelKey? pk = null;
            var uniques = new List<ModelKey>();
            var checks = new List<ModelCheck>();
            var fks = new List<ModelFk>();
            foreach (var (name, k) in t.Constraints)
            {
                switch (k.Type)
                {
                    case "P": pk = new ModelKey { Name = name, SystemNamed = k.Generated, Columns = k.Columns.ToArray() }; break;
                    case "U": uniques.Add(new ModelKey { Name = name, SystemNamed = k.Generated, Columns = k.Columns.ToArray() }); break;
                    case "C":
                        if (k.Condition == null) break;
                        var nonNull = t.Columns.Where(x => !x.Nullable).Select(x => x.Name);
                        if (IsNotNullCheck(k.Condition, nonNull)) break;
                        checks.Add(new ModelCheck { Name = name, SystemNamed = k.Generated, Expression = k.Condition.Trim(), Columns = ColumnsIn(k.Condition, colNames) });
                        break;
                    case "R" when k.RefOwner != null && k.RefName != null:
                        fks.Add(new ModelFk
                        {
                            Name = name, SystemNamed = k.Generated, Columns = k.Columns.ToArray(), RefSchema = k.RefOwner, RefTable = k.RefName,
                            RefColumns = refColumns.GetValueOrDefault((t.Schema, name))?.ToArray() ?? [], OnDelete = k.Delete ?? "NO ACTION", OnUpdate = "NO ACTION", Deferrable = k.Deferrable,
                        });
                        break;
                }
            }
            modelTables.Add(new ModelTable
            {
                Schema = t.Schema, Name = t.Name,
                Columns = t.Columns.Select(x => x.Computed == null ? x : x with { Uses = ColumnsIn(x.Computed, colNames) }).ToArray(),
                PrimaryKey = pk, Uniques = uniques.ToArray(), Checks = checks.ToArray(), Fks = fks.ToArray(),
                Indexes = t.Indexes.Select(kv => new ModelIndex
                {
                    Name = kv.Key, Unique = kv.Value.Unique, Kind = kv.Value.Kind,
                    Columns = kv.Value.Columns.Select(x => x.Column).ToArray(), Descending = kv.Value.Columns.Select(x => x.Desc).ToArray(),
                    // The columns a function-based part reads: a change to one of them takes the index down and back up.
                    Include = kv.Value.Columns.Where(x => !colNames.Contains(x.Column)).SelectMany(x => ColumnsIn(x.Column, colNames)).Distinct().ToArray() is { Length: > 0 } reads ? reads : null,
                }).ToArray(),
            });
        }

        var modules = new List<ModelModule>();
        foreach (var (owner, name, text) in viewText)
            modules.Add(new ModelModule
            {
                Schema = owner, Name = name, Kind = ModuleKind.View,
                Definition = text == null ? null : OracleMetadata.ViewDefinition(N(owner, name), viewCols.GetValueOrDefault((owner, name)) ?? [], text),
                References = references.GetValueOrDefault((owner, name))?.Distinct().ToArray() ?? [],
            });
        var schemaTriggers = 0;
        foreach (var ((owner, name, type), sb) in source)
        {
            var unit = sb.ToString();
            string? table = null;
            var refs = references.GetValueOrDefault((owner, name))?.Distinct().ToList() ?? [];
            if (type == "TRIGGER")
            {
                if (!triggers.TryGetValue((owner, name), out var trg) || trg.BaseType is not ("TABLE" or "VIEW") || trg.Table == null) { schemaTriggers++; continue; }
                table = trg.Table;
                if (trg.TableOwner != null && !refs.Contains(new ModelRef(trg.TableOwner, trg.Table))) refs.Add(new ModelRef(trg.TableOwner, trg.Table));
            }
            modules.Add(new ModelModule
            {
                Schema = owner, Name = name, Table = table,
                Kind = type switch { "PROCEDURE" => ModuleKind.Procedure, "FUNCTION" => ModuleKind.Function, _ => ModuleKind.Trigger },
                Definition = OracleMetadata.IsWrapped(unit) ? null : OracleMetadata.UnitDefinition(N(owner, name), unit),
                References = refs.ToArray(),
            });
        }
        if (schemaTriggers > 0) notCompared.Add($"{schemaTriggers} schema or database trigger(s) are not compared");

        return new SchemaModel
        {
            Engine = Engine.Oracle, ServerVersion = c.ServerVersion, MajorVersion = major,
            Database = c is OracleConnection oc ? (oc.ServiceName ?? oc.DatabaseName ?? "").ToUpperInvariant() : "",
            CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Schemas = schemas.ToArray(), Tables = modelTables.ToArray(), Modules = modules.ToArray(), Sequences = sequences.ToArray(),
            NotCompared = notCompared.ToArray(),
        };
    }

    // ---------------------------------------------------------------------------------------------- tokens

    /// <summary>Token text without trivia: words in upper case, a quoted name that could be written bare unquoted (the same name to Oracle).</summary>
    static List<string> Canonical(string sql) =>
        OracleTokenizer.Tokenize(sql).Where(t => !t.IsTrivia).Select(t => t.Kind switch
        {
            OracleTokenKind.Word => t.Text.ToUpperInvariant(),
            OracleTokenKind.QuotedIdent when OracleIdentifiers.Identity(t) is var inner && OracleIdentifiers.CanBeBare(inner) => inner,
            _ => t.Text,
        }).ToList();

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

    static readonly string[] Kinds = ["VIEW", "PROCEDURE", "FUNCTION", "TRIGGER", "PACKAGE", "TYPE"];

    /// <summary>CREATE [OR REPLACE] [EDITIONABLE | NONEDITIONABLE] [[NO] FORCE] goes; the trailing <c>/</c> and <c>;</c> too.</summary>
    public string NormalizeBody(string definition)
    {
        var t = Canonical(definition);
        var k = t.FindIndex(x => Kinds.Contains(x));
        var body = new List<string> { "CREATE" };
        body.AddRange(k >= 0 ? t.Skip(k) : t);
        while (body.Count > 1 && body[^1] is "/" or ";") body.RemoveAt(body.Count - 1);
        return string.Join(" ", body);
    }

    public string WithoutHeader(string definition)
    {
        var code = OracleTokenizer.Tokenize(definition).Where(t => !t.IsTrivia).ToList();
        var k = code.FindIndex(t => Kinds.Any(t.Is));
        var text = k < 0 ? definition : definition[code[k].Offset..];
        // The unit terminator is how Querithm writes it, not part of the object.
        var trimmed = text.TrimEnd();
        if (trimmed.EndsWith("\n/", StringComparison.Ordinal)) trimmed = trimmed[..^2].TrimEnd();
        return trimmed;
    }

    public string RenameSchema(string sql, string from, string to)
    {
        var tokens = OracleTokenizer.Tokenize(sql);
        var sb = new StringBuilder(sql.Length + 16);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var isName = t.Kind is OracleTokenKind.Word or OracleTokenKind.QuotedIdent;
            if (isName && OracleIdentifiers.Identity(t) == from && Next(tokens, i) is { } n && n.IsP(".") && !(Prev(tokens, i) is { } p && p.IsP(".")))
                sb.Append(t.Kind == OracleTokenKind.Word && OracleIdentifiers.CanBeBare(to) ? to : Q(to));
            else sb.Append(t.Text);
        }
        return sb.ToString();
    }

    static OracleToken? Next(List<OracleToken> tokens, int i)
    {
        for (var j = i + 1; j < tokens.Count; j++) if (!tokens[j].IsTrivia) return tokens[j];
        return null;
    }

    static OracleToken? Prev(List<OracleToken> tokens, int i)
    {
        for (var j = i - 1; j >= 0; j--) if (!tokens[j].IsTrivia) return tokens[j];
        return null;
    }

    // ---------------------------------------------------------------------------------------------- DDL

    static NotSupportedException Unsupported(string what) => new($"Oracle schema compare does not script {what}.");

    /// <summary>A schema is a user: a schema-only account (18c+), which nobody signs in as, owns the new objects.</summary>
    public string CreateSchema(string schema) => $"CREATE USER {Q(schema)} NO AUTHENTICATION;";
    public string DropSchema(string schema) => $"DROP USER {Q(schema)};";
    public string CreateExtension(string name) => throw Unsupported("extensions");
    public string DropExtension(string name) => throw Unsupported("extensions");
    public string CreateType(ModelType type) => throw Unsupported("object types");
    public string DropType(ModelType type) => throw Unsupported("object types");
    public string? AddEnumValue(ModelType type, string value, string? after) => null;

    public string CreateSequence(ModelSequence s)
    {
        var sb = new StringBuilder($"CREATE SEQUENCE {N(s.Schema, s.Name)} START WITH {s.Start} INCREMENT BY {s.Increment}");
        if (s.Min != null) sb.Append($" MINVALUE {s.Min}");
        if (s.Max != null) sb.Append($" MAXVALUE {s.Max}");
        sb.Append(s.Cycle ? " CYCLE" : " NOCYCLE").Append(';');
        return sb.ToString();
    }

    /// <summary>ALTER SEQUENCE takes every option but START WITH (the next value stays where it is).</summary>
    public string AlterSequence(ModelSequence from, ModelSequence to)
    {
        var sb = new StringBuilder($"ALTER SEQUENCE {N(to.Schema, to.Name)} INCREMENT BY {to.Increment}");
        sb.Append(to.Min != null ? $" MINVALUE {to.Min}" : " NOMINVALUE");
        sb.Append(to.Max != null ? $" MAXVALUE {to.Max}" : " NOMAXVALUE");
        sb.Append(to.Cycle ? " CYCLE" : " NOCYCLE").Append(';');
        return sb.ToString();
    }

    public string DropSequence(ModelSequence s) => $"DROP SEQUENCE {N(s.Schema, s.Name)};";

    static bool ImpliesNotNull(ModelColumn c) => c.Identity != null || c.Default?.StartsWith("ON NULL ", StringComparison.Ordinal) == true;

    /// <summary>The whole column as CREATE TABLE and ADD take it: type, virtual expression, identity or default, NOT NULL.</summary>
    public static string ColumnDef(ModelColumn c)
    {
        var sb = new StringBuilder($"{Q(c.Name)} {c.Type}");
        if (c.Computed != null) sb.Append($" GENERATED ALWAYS AS ({c.Computed}) VIRTUAL");
        else if (c.Identity != null) sb.Append($" GENERATED {c.Identity} AS IDENTITY");
        else if (c.Default != null) sb.Append($" DEFAULT {c.Default}");
        if (!c.Nullable && !ImpliesNotNull(c)) sb.Append(" NOT NULL");
        return sb.ToString();
    }

    static string Cols(IEnumerable<string> names) => string.Join(", ", names.Select(Q));
    static string Named(string name, bool system) => system ? "" : $"CONSTRAINT {Q(name)} ";

    public string CreateTable(ModelTable t)
    {
        var parts = t.Columns.Select(ColumnDef).ToList();
        if (t.PrimaryKey is { } pk) parts.Add($"{Named(pk.Name, pk.SystemNamed)}PRIMARY KEY ({Cols(pk.Columns)})");
        parts.AddRange(t.Uniques.Select(u => $"{Named(u.Name, u.SystemNamed)}UNIQUE ({Cols(u.Columns)})"));
        parts.AddRange(t.Checks.Select(c => $"{Named(c.Name, c.SystemNamed)}CHECK ({c.Expression})"));
        return $"CREATE TABLE {N(t.Schema, t.Name)} (\n    {string.Join(",\n    ", parts)}\n);";
    }

    /// <summary>Into the recycle bin: FLASHBACK TABLE … TO BEFORE DROP brings it back until it is purged.</summary>
    public string DropTable(ModelTable t) => $"DROP TABLE {N(t.Schema, t.Name)};";
    public string AddColumn(ModelTable t, ModelColumn c) => $"ALTER TABLE {N(t.Schema, t.Name)} ADD ({ColumnDef(c)});";
    public string DropColumn(ModelTable t, ModelColumn c) => $"ALTER TABLE {N(t.Schema, t.Name)} DROP COLUMN {Q(c.Name)};";
    public string RenameColumn(ModelTable t, string from, string to) => $"ALTER TABLE {N(t.Schema, t.Name)} RENAME COLUMN {Q(from)} TO {Q(to)};";

    /// <summary>
    /// One MODIFY per change Oracle takes there: the type, NULL / NOT NULL (only when it changes: Oracle refuses a MODIFY to
    /// what the column already is), the identity kind, dropping the identity. Defaults go through <see cref="AddDefault"/>.
    /// </summary>
    public string[] AlterColumn(ModelTable t, ModelColumn from, ModelColumn to)
    {
        var table = N(t.Schema, t.Name);
        var list = new List<string>();
        if (!string.Equals(from.Type, to.Type, StringComparison.OrdinalIgnoreCase)) list.Add($"ALTER TABLE {table} MODIFY ({Q(to.Name)} {to.Type});");
        if (from.Identity != to.Identity)
        {
            if (to.Identity == null) list.Add($"ALTER TABLE {table} MODIFY ({Q(to.Name)} DROP IDENTITY);");
            else if (from.Identity != null) list.Add($"ALTER TABLE {table} MODIFY ({Q(to.Name)} GENERATED {to.Identity} AS IDENTITY);");
        }
        // An identity or ON NULL default makes the column NOT NULL by itself.
        var wasNotNull = !from.Nullable;
        var nowNotNull = !to.Nullable;
        if (wasNotNull != nowNotNull && !(nowNotNull && ImpliesNotNull(to)))
            list.Add($"ALTER TABLE {table} MODIFY ({Q(to.Name)} {(nowNotNull ? "NOT NULL" : "NULL")});");
        return list.ToArray();
    }

    public string? AlterColumnRefusal(ModelColumn from, ModelColumn to) =>
        from.Identity == null && to.Identity != null
            ? $"{to.Name} becomes an identity column, which Oracle cannot make of an existing column: add a new identity column and move the values by hand"
            : null;

    public string? DropDefault(ModelTable t, ModelColumn c) => c.Default == null ? null : $"ALTER TABLE {N(t.Schema, t.Name)} MODIFY ({Q(c.Name)} DEFAULT NULL);";
    public string? AddDefault(ModelTable t, ModelColumn c) => c.Default == null ? null : $"ALTER TABLE {N(t.Schema, t.Name)} MODIFY ({Q(c.Name)} DEFAULT {c.Default});";

    public string AddKey(ModelTable t, ModelKey k, bool primary) =>
        $"ALTER TABLE {N(t.Schema, t.Name)} ADD {Named(k.Name, k.SystemNamed)}{(primary ? "PRIMARY KEY" : "UNIQUE")} ({Cols(k.Columns)});";

    public string DropConstraint(ModelTable t, string name) => $"ALTER TABLE {N(t.Schema, t.Name)} DROP CONSTRAINT {Q(name)};";

    public string AddCheck(ModelTable t, ModelCheck c) => $"ALTER TABLE {N(t.Schema, t.Name)} ADD {Named(c.Name, c.SystemNamed)}CHECK ({c.Expression});";

    /// <summary>A key part that names one of the table's columns is quoted; anything else is a function-based expression written as Oracle keeps it.</summary>
    public string CreateIndex(ModelTable t, ModelIndex i)
    {
        var names = t.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var parts = i.Columns.Select((c, n) => (names.Contains(c) ? Q(c) : c) + (i.Descending is { } d && n < d.Length && d[n] ? " DESC" : ""));
        var kind = i.Kind ?? "NORMAL";
        var prefix = i.Unique ? "UNIQUE " : kind.StartsWith("BITMAP", StringComparison.Ordinal) ? "BITMAP " : "";
        return $"CREATE {prefix}INDEX {N(t.Schema, i.Name)} ON {N(t.Schema, t.Name)} ({string.Join(", ", parts)}){(kind.EndsWith("REVERSE", StringComparison.Ordinal) ? " REVERSE" : "")};";
    }

    public string DropIndex(ModelTable t, ModelIndex i) => $"DROP INDEX {N(t.Schema, i.Name)};";

    /// <summary>Oracle has ON DELETE CASCADE and SET NULL only, and no ON UPDATE.</summary>
    public string AddFk(ModelTable t, ModelFk f) =>
        $"ALTER TABLE {N(t.Schema, t.Name)} ADD {Named(f.Name, f.SystemNamed)}FOREIGN KEY ({Cols(f.Columns)}) REFERENCES {N(f.RefSchema, f.RefTable)} ({Cols(f.RefColumns)})" +
        (f.OnDelete is "CASCADE" or "SET NULL" ? $" ON DELETE {f.OnDelete}" : "") + (f.Deferrable ? " DEFERRABLE" : "") + ";";

    /// <summary>The definition as read (CREATE OR REPLACE, a view ending in <c>;</c>, a unit in <c>/</c>).</summary>
    public string CreateModule(ModelModule m, int targetMajor)
    {
        var def = (m.Definition ?? throw new InvalidOperationException($"{m.Schema}.{m.Name} has no definition.")).Trim();
        var plsql = m.Kind is not (ModuleKind.View or ModuleKind.MaterializedView);
        if (plsql && !def.EndsWith("\n/", StringComparison.Ordinal)) def += "\n/";
        if (!plsql && !def.EndsWith(';')) def += ";";
        return def;
    }

    /// <summary>CREATE OR REPLACE works for every kind compared; only a change of kind (a function that became a procedure) drops first.</summary>
    public bool NeedsDropBeforeCreate(ModelModule? existing, ModelModule wanted, int targetMajor) => existing != null && existing.Kind != wanted.Kind;

    public string DropModule(ModelModule m) => m.Kind switch
    {
        ModuleKind.View => $"DROP VIEW {N(m.Schema, m.Name)};",
        ModuleKind.MaterializedView => $"DROP MATERIALIZED VIEW {N(m.Schema, m.Name)};",
        ModuleKind.Procedure => $"DROP PROCEDURE {N(m.Schema, m.Name)};",
        ModuleKind.Trigger => $"DROP TRIGGER {N(m.Schema, m.Name)};",
        ModuleKind.Package => $"DROP PACKAGE {N(m.Schema, m.Name)};",
        ModuleKind.PackageBody => $"DROP PACKAGE BODY {N(m.Schema, m.Name)};",
        _ => $"DROP FUNCTION {N(m.Schema, m.Name)};",
    };

    /// <summary>A view or unit on a dropped or changed object becomes invalid instead of blocking the change.</summary>
    public bool DependencyBlocks(ModelModule dependent) => false;

    /// <summary>Invalid views and units recompile on their next use; nothing is run for them.</summary>
    public string? RefreshModule(ModelModule m) => null;

    /// <summary>ALTER TABLE … RENAME TO; views and routines have no RENAME (Core recreates them), a sequence or type only RENAME in one's own schema.</summary>
    public string RenameObject(RefKind kind, string schema, string name, string newName) => kind switch
    {
        RefKind.Table => $"ALTER TABLE {N(schema, name)} RENAME TO {Q(newName)};",
        RefKind.View or RefKind.Procedure or RefKind.Function =>
            throw new NotSupportedException("Oracle has no RENAME for views and routines: Querithm creates them under the new name and drops the old one."),
        _ => throw new NotSupportedException($"Oracle renames a {kind.ToString().ToLowerInvariant()} only in your own schema (RENAME … TO): rename it by hand, then update what uses it."),
    };

    public string RenameIdentifier(string expression, string from, string to)
    {
        var sb = new StringBuilder(expression.Length + 8);
        foreach (var t in OracleTokenizer.Tokenize(expression))
        {
            var isName = t.Kind is OracleTokenKind.Word or OracleTokenKind.QuotedIdent;
            sb.Append(isName && OracleIdentifiers.Identity(t) == from
                ? t.Kind == OracleTokenKind.Word && OracleIdentifiers.CanBeBare(to) ? to : Q(to)
                : t.Text);
        }
        return sb.ToString();
    }

    public string Wrap(IReadOnlyList<MigrationStep> steps, string header)
    {
        var sb = new StringBuilder(header.TrimEnd()).Append("\n\n");
        sb.Append("-- Oracle commits every DDL statement on its own: this script is not one transaction. If a step fails, the steps\n");
        sb.Append("-- before it stay applied; read it first and keep a backup (a dropped table stays in the recycle bin until purged).\n\n");
        foreach (var s in steps)
            sb.Append("-- ").Append(CommentText.Safe(s.Comment)).Append('\n').Append(s.Sql.TrimEnd()).Append("\n\n");
        return sb.ToString();
    }
}
