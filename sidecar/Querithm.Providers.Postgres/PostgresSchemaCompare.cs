using System.Data.Common;
using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;

namespace Querithm.Providers.Postgres;

/// <summary>
/// Schema compare for PostgreSQL: the model from the catalogs (pg_get_*def for the server's own canonical
/// text), normalisation on <see cref="PgTokenizer"/> tokens (function bodies included), and one BEGIN … COMMIT;
/// enum values are added before it because a value added in a transaction cannot be used in that transaction.
/// </summary>
public sealed class PostgresSchemaCompare : ISchemaCompareDialect
{
    static string Q(string s) => PostgresImport.Q(s);
    static string N(string schema, string name) => $"{Q(schema)}.{Q(name)}";
    static string Lit(string s) => PostgresDataEdit.Literal(s);

    public StringComparer Names => StringComparer.Ordinal;
    public bool TransactionalDdl => true;
    public bool AlterTypeNeedsDependentsDropped => false;
    public bool RenameColumnNeedsDependentsDropped => false;

    // ---------------------------------------------------------------------------------------------- model

    const string UserNs = "n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg\\_%'";
    static string NotExt(string catalog, string oid) =>
        $"NOT EXISTS (SELECT 1 FROM pg_depend e WHERE e.classid = '{catalog}'::regclass AND e.objid = {oid} AND e.deptype = 'e')";

    static readonly string ModelSql = $"""
        SELECT current_database(), current_setting('server_version'), current_setting('server_version_num')::int;
        SELECT n.nspname FROM pg_namespace n WHERE {UserNs} AND {NotExt("pg_namespace", "n.oid")} ORDER BY 1;
        SELECT extname FROM pg_extension WHERE extname <> 'plpgsql' ORDER BY 1;
        SELECT c.oid::bigint, n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r','p') AND NOT c.relispartition AND {UserNs} AND {NotExt("pg_class", "c.oid")};
        SELECT a.attrelid::bigint, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, pg_get_expr(ad.adbin, ad.adrelid),
               a.attidentity::text, a.attgenerated::text,
               CASE WHEN a.attcollation <> 0 AND a.attcollation <> t.typcollation THEN quote_ident(cn.nspname) || '.' || quote_ident(co.collname) END
        FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_type t ON t.oid = a.atttypid
        LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
        LEFT JOIN pg_collation co ON co.oid = a.attcollation LEFT JOIN pg_namespace cn ON cn.oid = co.collnamespace
        WHERE c.relkind IN ('r','p') AND NOT c.relispartition AND a.attnum > 0 AND NOT a.attisdropped AND {UserNs}
        ORDER BY a.attrelid, a.attnum;
        SELECT con.conrelid::bigint, con.conname, con.contype::text, pg_get_constraintdef(con.oid),
               ARRAY(SELECT a.attname FROM unnest(con.conkey) WITH ORDINALITY k(n, o) JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.n ORDER BY k.o)::text[],
               rn.nspname, rc.relname,
               ARRAY(SELECT a.attname FROM unnest(con.confkey) WITH ORDINALITY k(n, o) JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.n ORDER BY k.o)::text[],
               con.confdeltype::text, con.confupdtype::text, con.condeferrable
        FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_class rc ON rc.oid = con.confrelid LEFT JOIN pg_namespace rn ON rn.oid = rc.relnamespace
        WHERE con.contype IN ('p','u','c','f') AND c.relkind IN ('r','p') AND NOT c.relispartition AND {UserNs}
        ORDER BY con.conrelid, con.conname;
        SELECT i.indrelid::bigint, ic.relname, i.indisunique, am.amname, pg_get_indexdef(i.indexrelid), pg_get_expr(i.indpred, i.indrelid),
               ARRAY(SELECT a.attname FROM unnest(i.indkey) k(n) JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.n)::text[]
        FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid JOIN pg_class c ON c.oid = i.indrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace JOIN pg_am am ON am.oid = ic.relam
        WHERE c.relkind IN ('r','p') AND NOT c.relispartition AND {UserNs}
          AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = i.indexrelid AND k.conrelid = i.indrelid AND k.contype IN ('p','u','x'));
        SELECT c.oid::bigint, n.nspname, c.relname, c.relkind::text, pg_get_viewdef(c.oid, true),
               ARRAY(SELECT a.attname || ' ' || format_type(a.atttypid, a.atttypmod) FROM pg_attribute a
                     WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum)::text[]
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('v','m') AND {UserNs} AND {NotExt("pg_class", "c.oid")};
        SELECT p.oid::bigint, n.nspname, p.proname, p.prokind::text, pg_get_functiondef(p.oid), pg_get_function_identity_arguments(p.oid),
               pg_get_function_result(p.oid)
        FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE p.prokind IN ('f','p') AND {UserNs} AND {NotExt("pg_proc", "p.oid")};
        SELECT t.oid::bigint, n.nspname, c.relname, t.tgname, pg_get_triggerdef(t.oid, true), pn.nspname, p.proname
        FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_proc p ON p.oid = t.tgfoid JOIN pg_namespace pn ON pn.oid = p.pronamespace
        WHERE NOT t.tgisinternal AND {UserNs} AND NOT c.relispartition;
        SELECT DISTINCT r.ev_class::bigint, dn.nspname, dc.relname
        FROM pg_rewrite r JOIN pg_depend d ON d.objid = r.oid AND d.classid = 'pg_rewrite'::regclass AND d.refclassid = 'pg_class'::regclass
        JOIN pg_class dc ON dc.oid = d.refobjid JOIN pg_namespace dn ON dn.oid = dc.relnamespace
        WHERE d.refobjid <> r.ev_class
        UNION
        SELECT DISTINCT r.ev_class::bigint, pn.nspname, p.proname
        FROM pg_rewrite r JOIN pg_depend d ON d.objid = r.oid AND d.classid = 'pg_rewrite'::regclass AND d.refclassid = 'pg_proc'::regclass
        JOIN pg_proc p ON p.oid = d.refobjid JOIN pg_namespace pn ON pn.oid = p.pronamespace
        UNION
        SELECT DISTINCT d.objid::bigint, dn.nspname, dc.relname
        FROM pg_depend d JOIN pg_class dc ON dc.oid = d.refobjid JOIN pg_namespace dn ON dn.oid = dc.relnamespace
        WHERE d.classid = 'pg_proc'::regclass AND d.refclassid = 'pg_class'::regclass AND d.deptype = 'n';
        SELECT n.nspname, c.relname, format_type(s.seqtypid, NULL), s.seqstart::text, s.seqincrement::text, s.seqmin::text, s.seqmax::text, s.seqcycle
        FROM pg_sequence s JOIN pg_class c ON c.oid = s.seqrelid JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE {UserNs} AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype IN ('i','e'));
        SELECT n.nspname, t.typname, t.typtype::text,
               ARRAY(SELECT e.enumlabel FROM pg_enum e WHERE e.enumtypid = t.oid ORDER BY e.enumsortorder)::text[],
               format_type(t.typbasetype, t.typtypmod), t.typnotnull, t.typdefault,
               ARRAY(SELECT pg_get_constraintdef(k.oid) FROM pg_constraint k WHERE k.contypid = t.oid ORDER BY k.conname)::text[]
        FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
        WHERE t.typtype IN ('e','d') AND {UserNs} AND {NotExt("pg_type", "t.oid")};
        SELECT (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relispartition AND {UserNs})::int,
               (SELECT count(*) FROM pg_constraint k JOIN pg_namespace n ON n.oid = k.connamespace WHERE k.contype = 'x' AND {UserNs})::int,
               (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE p.prokind IN ('a','w') AND {UserNs})::int,
               (SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace JOIN pg_class c ON c.oid = t.typrelid
                WHERE t.typtype = 'c' AND c.relkind = 'c' AND {UserNs})::int,
               (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'p' AND {UserNs})::int,
               (SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'm' AND {UserNs})::int;
        """;

    public async Task<SchemaModel> ReadModelAsync(DbConnection c, CancellationToken ct)
    {
        await using (var probe = c.CreateCommand())
        {
            probe.CommandText = "SELECT current_setting('server_version_num')::int";
            if ((int)(await probe.ExecuteScalarAsync(ct).ConfigureAwait(false))! < 120000)
                throw new NotSupportedException("Schema compare needs PostgreSQL 12 or later (generated columns and the catalogs it reads).");
        }
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
        string[] A(int i) => r.IsDBNull(i) ? [] : r.GetFieldValue<string[]>(i);

        string db = "", version = "";
        var major = 0;
        await Each(() => { db = r.GetString(0); version = r.GetString(1); major = r.GetInt32(2) / 10000; });
        var schemas = new List<string>();
        await Each(() => schemas.Add(r.GetString(0)));
        var extensions = new List<string>();
        await Each(() => extensions.Add(r.GetString(0)));
        var tables = new Dictionary<long, (string Schema, string Name)>();
        await Each(() => tables[r.GetInt64(0)] = (r.GetString(1), r.GetString(2)));

        var cols = new Dictionary<long, List<ModelColumn>>();
        await Each(() =>
        {
            var identity = r.GetString(5) switch { "a" => "always", "d" => "by default", _ => null };
            var generated = r.GetString(6) == "s";
            List(cols, r.GetInt64(0)).Add(new ModelColumn
            {
                Name = r.GetString(1), Type = r.GetString(2), Nullable = r.GetBoolean(3), Identity = identity,
                Default = generated ? null : S(4), Computed = generated ? S(4) : null, Persisted = generated, Collation = S(7),
            });
        });

        var pks = new Dictionary<long, ModelKey>();
        var uniques = new Dictionary<long, List<ModelKey>>();
        var checks = new Dictionary<long, List<ModelCheck>>();
        var fks = new Dictionary<long, List<ModelFk>>();
        await Each(() =>
        {
            var oid = r.GetInt64(0);
            var name = r.GetString(1);
            var keyCols = A(4);
            switch (r.GetString(2))
            {
                case "p": pks[oid] = new ModelKey { Name = name, Columns = keyCols }; break;
                case "u": List(uniques, oid).Add(new ModelKey { Name = name, Columns = keyCols }); break;
                case "c": List(checks, oid).Add(new ModelCheck { Name = name, Expression = r.GetString(3), Columns = keyCols }); break;
                default:
                    List(fks, oid).Add(new ModelFk
                    {
                        Name = name, Columns = keyCols, RefSchema = r.GetString(5), RefTable = r.GetString(6), RefColumns = A(7),
                        OnDelete = Action(r.GetString(8)), OnUpdate = Action(r.GetString(9)), Deferrable = r.GetBoolean(10),
                    });
                    break;
            }
        });

        var indexes = new Dictionary<long, List<ModelIndex>>();
        await Each(() => List(indexes, r.GetInt64(0)).Add(new ModelIndex
        {
            Name = r.GetString(1), Unique = r.GetBoolean(2), Kind = r.GetString(3), Definition = r.GetString(4), Filter = S(5), Columns = A(6),
        }));

        var modules = new Dictionary<long, ModelModule>();
        await Each(() =>
        {
            var mat = r.GetString(3) == "m";
            var header = mat ? "CREATE MATERIALIZED VIEW " : "CREATE OR REPLACE VIEW ";
            var body = r.GetString(4).TrimEnd().TrimEnd(';');
            modules[r.GetInt64(0)] = new ModelModule
            {
                Schema = r.GetString(1), Name = r.GetString(2), Kind = mat ? ModuleKind.MaterializedView : ModuleKind.View,
                Definition = $"{header}{N(r.GetString(1), r.GetString(2))} AS\n{body};", Columns = A(5),
            };
        });
        await Each(() => modules[r.GetInt64(0)] = new ModelModule
        {
            Schema = r.GetString(1), Name = r.GetString(2), Kind = r.GetString(3) == "p" ? ModuleKind.Procedure : ModuleKind.Function,
            Definition = r.GetString(4).TrimEnd() is var d && d.EndsWith(';') ? d : d + ";", Signature = r.GetString(5), Returns = S(6),
        });
        var triggers = new List<ModelModule>();
        await Each(() => triggers.Add(new ModelModule
        {
            Schema = r.GetString(1), Name = r.GetString(3), Table = r.GetString(2), Kind = ModuleKind.Trigger, Definition = r.GetString(4).TrimEnd() + ";",
            References = [new ModelRef(r.GetString(5), r.GetString(6)), new ModelRef(r.GetString(1), r.GetString(2))],
        }));
        var refs = new Dictionary<long, List<ModelRef>>();
        await Each(() => List(refs, r.GetInt64(0)).Add(new ModelRef(r.GetString(1), r.GetString(2))));

        var sequences = new List<ModelSequence>();
        await Each(() => sequences.Add(new ModelSequence
        {
            Schema = r.GetString(0), Name = r.GetString(1), Type = r.GetString(2), Start = r.GetString(3), Increment = r.GetString(4),
            Min = r.GetString(5), Max = r.GetString(6), Cycle = r.GetBoolean(7),
        }));
        var types = new List<ModelType>();
        await Each(() =>
        {
            if (r.GetString(2) == "e")
            {
                types.Add(new ModelType { Schema = r.GetString(0), Name = r.GetString(1), Kind = "enum", Values = A(3) });
                return;
            }
            var def = new StringBuilder(r.GetString(4));
            if (S(6) is { } dflt) def.Append(" DEFAULT ").Append(dflt);
            if (r.GetBoolean(5)) def.Append(" NOT NULL");
            foreach (var chk in A(7)) def.Append(' ').Append(chk);
            types.Add(new ModelType { Schema = r.GetString(0), Name = r.GetString(1), Kind = "domain", Definition = def.ToString() });
        });
        var notCompared = new List<string>();
        await Each(() =>
        {
            void Note(int i, string what) { var n = r.GetInt32(i); if (n > 0) notCompared.Add($"{n} {what}"); }
            Note(0, "partitions are not compared (only their parent tables)");
            Note(1, "exclusion constraints are not compared");
            Note(2, "aggregates and window functions are not compared");
            Note(3, "composite types are not compared");
            Note(4, "partitioned tables are compared without their PARTITION BY clause");
            Note(5, "indexes on materialized views are not compared");
        });

        return new SchemaModel
        {
            Engine = Engine.Pg, ServerVersion = version, MajorVersion = major, Database = db, CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Schemas = schemas.ToArray(), Extensions = extensions.ToArray(),
            Tables = tables.Select(kv => new ModelTable
            {
                Schema = kv.Value.Schema, Name = kv.Value.Name, Columns = WithUses(cols.GetValueOrDefault(kv.Key) ?? []),
                PrimaryKey = pks.GetValueOrDefault(kv.Key), Uniques = (uniques.GetValueOrDefault(kv.Key) ?? []).ToArray(),
                Checks = (checks.GetValueOrDefault(kv.Key) ?? []).ToArray(), Fks = (fks.GetValueOrDefault(kv.Key) ?? []).ToArray(),
                Indexes = (indexes.GetValueOrDefault(kv.Key) ?? []).ToArray(),
            }).ToArray(),
            Modules = modules.Select(kv => kv.Value with { References = (refs.GetValueOrDefault(kv.Key) ?? []).Distinct().ToArray() }).Concat(triggers).ToArray(),
            Sequences = sequences.ToArray(), Types = types.ToArray(), NotCompared = notCompared.ToArray(),
        };
    }

    /// <summary>The table's columns a generated column's expression names (on tokens).</summary>
    static ModelColumn[] WithUses(List<ModelColumn> cols)
    {
        var names = cols.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return cols.Select(c => c.Computed == null ? c : c with
        {
            Uses = PgTokenizer.Tokenize(c.Computed)
                .Select(t => t.Kind == PgTokenKind.Word ? t.Text.ToLowerInvariant() : t.Kind == PgTokenKind.QuotedIdent && t.Text.Length >= 2 ? t.Text[1..^1].Replace("\"\"", "\"") : null)
                .Where(n => n != null && names.Contains(n)).Select(n => n!).Distinct().ToArray(),
        }).ToArray();
    }

    static string Action(string code) => code switch
    {
        "r" => "RESTRICT", "c" => "CASCADE", "n" => "SET NULL", "d" => "SET DEFAULT", _ => "NO ACTION",
    };

    static List<T> List<T>(Dictionary<long, List<T>> map, long key)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        return list;
    }

    // ---------------------------------------------------------------------------------------------- tokens

    static bool PlainName(string s) =>
        s.Length > 0 && (char.IsLower(s[0]) || s[0] == '_') && s.All(ch => char.IsLower(ch) || char.IsDigit(ch) || ch == '_' || ch == '$');

    /// <summary>Token text without trivia: words lower-cased (PostgreSQL folds them), "plain" names unquoted, bodies normalised inside.</summary>
    static List<string> Canonical(string sql)
    {
        var list = new List<string>();
        foreach (var t in PgTokenizer.Tokenize(sql))
        {
            if (t.IsTrivia) continue;
            list.Add(t.Kind switch
            {
                PgTokenKind.Word => t.Text.ToLowerInvariant(),
                PgTokenKind.QuotedIdent when t.Text.Length >= 2 && t.Text[1..^1] is var inner && PlainName(inner) => inner,
                PgTokenKind.DollarString when DollarBody(t.Text) is { } body => "$$ " + string.Join(" ", Canonical(body)) + " $$",
                _ => t.Text,
            });
        }
        return list;
    }

    /// <summary>The text inside $tag$ … $tag$, or null for an unterminated string.</summary>
    static string? DollarBody(string text)
    {
        var close = text.IndexOf('$', 1);
        if (close < 0) return null;
        var tag = text[..(close + 1)];
        return text.Length >= 2 * tag.Length && text.EndsWith(tag, StringComparison.Ordinal) ? text[tag.Length..^tag.Length] : null;
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

    public string NormalizeBody(string definition)
    {
        var t = Canonical(definition);
        var i = 0;
        if (i < t.Count && t[i] is "create" or "alter")
        {
            i++;
            if (i + 1 < t.Count && t[i] == "or" && t[i + 1] == "replace") i += 2;
        }
        var body = new List<string> { "create" };
        body.AddRange(t.Skip(i));
        while (body.Count > 1 && body[^1] == ";") body.RemoveAt(body.Count - 1);
        return string.Join(" ", body);
    }

    public string WithoutHeader(string definition)
    {
        var tokens = PgTokenizer.Tokenize(definition).Where(t => !t.IsTrivia).ToList();
        if (tokens.Count == 0 || !(tokens[0].Is("CREATE") || tokens[0].Is("ALTER"))) return definition;
        var i = tokens.Count > 2 && tokens[1].Is("OR") && tokens[2].Is("REPLACE") ? 3 : 1;
        return i < tokens.Count ? definition[tokens[i].Offset..] : "";
    }

    public string RenameSchema(string sql, string from, string to)
    {
        var tokens = PgTokenizer.Tokenize(sql);
        var sb = new StringBuilder(sql.Length + 16);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var name = t.Kind switch
            {
                PgTokenKind.Word => t.Text.ToLowerInvariant(),
                PgTokenKind.QuotedIdent when t.Text.Length >= 2 => t.Text[1..^1].Replace("\"\"", "\""),
                _ => null,
            };
            if (name == from && Next(tokens, i) is { } n && n.Text == "." && Prev(tokens, i)?.Text != ".")
                sb.Append(t.Kind == PgTokenKind.Word && PlainName(to) ? to : Q(to)); // keep the original quoting
            else if (t.Kind == PgTokenKind.DollarString && DollarBody(t.Text) is { } body)
            {
                var tag = t.Text[..(t.Text.IndexOf('$', 1) + 1)];
                sb.Append(tag).Append(RenameSchema(body, from, to)).Append(tag);
            }
            else sb.Append(t.Text);
        }
        return sb.ToString();
    }

    static PgToken? Next(List<PgToken> tokens, int i)
    {
        for (var j = i + 1; j < tokens.Count; j++) if (!tokens[j].IsTrivia) return tokens[j];
        return null;
    }

    static PgToken? Prev(List<PgToken> tokens, int i)
    {
        for (var j = i - 1; j >= 0; j--) if (!tokens[j].IsTrivia) return tokens[j];
        return null;
    }

    // ---------------------------------------------------------------------------------------------- DDL

    public string CreateSchema(string schema) => $"CREATE SCHEMA {Q(schema)};";
    public string DropSchema(string schema) => $"DROP SCHEMA {Q(schema)};";
    public string CreateExtension(string name) => $"CREATE EXTENSION IF NOT EXISTS {Q(name)};";
    public string DropExtension(string name) => $"DROP EXTENSION {Q(name)};";

    public string CreateType(ModelType type) => type.Kind == "enum"
        ? $"CREATE TYPE {N(type.Schema, type.Name)} AS ENUM ({string.Join(", ", (type.Values ?? []).Select(Lit))});"
        : $"CREATE DOMAIN {N(type.Schema, type.Name)} AS {type.Definition};";

    public string DropType(ModelType type) => $"DROP {(type.Kind == "enum" ? "TYPE" : "DOMAIN")} {N(type.Schema, type.Name)};";

    public string? AddEnumValue(ModelType type, string value, string? after) =>
        $"ALTER TYPE {N(type.Schema, type.Name)} ADD VALUE IF NOT EXISTS {Lit(value)}{(after != null ? $" AFTER {Lit(after)}" : "")};";

    public string CreateSequence(ModelSequence s) =>
        $"CREATE SEQUENCE {N(s.Schema, s.Name)} AS {s.Type} START WITH {s.Start} INCREMENT BY {s.Increment}{Bounds(s)};";

    static string Bounds(ModelSequence s) =>
        $"{(s.Min != null ? $" MINVALUE {s.Min}" : " NO MINVALUE")}{(s.Max != null ? $" MAXVALUE {s.Max}" : " NO MAXVALUE")}{(s.Cycle ? " CYCLE" : " NO CYCLE")}";

    public string AlterSequence(ModelSequence from, ModelSequence to) =>
        $"ALTER SEQUENCE {N(to.Schema, to.Name)} AS {to.Type} INCREMENT BY {to.Increment}{Bounds(to)};";

    public string DropSequence(ModelSequence s) => $"DROP SEQUENCE {N(s.Schema, s.Name)};";

    static string ColumnDef(ModelColumn c)
    {
        var sb = new StringBuilder($"{Q(c.Name)} {c.Type}");
        if (c.Collation != null) sb.Append($" COLLATE {c.Collation}");
        if (c.Computed != null) sb.Append($" GENERATED ALWAYS AS ({c.Computed}) STORED");
        if (c.Identity != null) sb.Append($" GENERATED {(c.Identity == "always" ? "ALWAYS" : "BY DEFAULT")} AS IDENTITY");
        if (!c.Nullable) sb.Append(" NOT NULL");
        if (c.Default != null) sb.Append($" DEFAULT {c.Default}");
        return sb.ToString();
    }

    static string KeyDef(ModelKey k, bool primary) =>
        $"CONSTRAINT {Q(k.Name)} {(primary ? "PRIMARY KEY" : "UNIQUE")} ({string.Join(", ", k.Columns.Select(Q))})";

    public string CreateTable(ModelTable t)
    {
        var parts = t.Columns.Select(ColumnDef).ToList();
        if (t.PrimaryKey is { } pk) parts.Add(KeyDef(pk, true));
        parts.AddRange(t.Uniques.Select(u => KeyDef(u, false)));
        parts.AddRange(t.Checks.Select(c => $"CONSTRAINT {Q(c.Name)} {c.Expression}"));
        return $"CREATE TABLE {N(t.Schema, t.Name)} (\n    {string.Join(",\n    ", parts)}\n);";
    }

    public string DropTable(ModelTable t) => $"DROP TABLE {N(t.Schema, t.Name)};";
    public string AddColumn(ModelTable t, ModelColumn c) => $"ALTER TABLE {N(t.Schema, t.Name)} ADD COLUMN {ColumnDef(c)};";
    public string DropColumn(ModelTable t, ModelColumn c) => $"ALTER TABLE {N(t.Schema, t.Name)} DROP COLUMN {Q(c.Name)};";
    public string RenameColumn(ModelTable t, string from, string to) => $"ALTER TABLE {N(t.Schema, t.Name)} RENAME COLUMN {Q(from)} TO {Q(to)};";

    public string[] AlterColumn(ModelTable t, ModelColumn from, ModelColumn to)
    {
        var table = N(t.Schema, t.Name);
        var col = Q(to.Name);
        var list = new List<string>();
        if (from.Type != to.Type || !Equals(from.Collation, to.Collation))
            list.Add($"ALTER TABLE {table} ALTER COLUMN {col} TYPE {to.Type}{(to.Collation != null ? $" COLLATE {to.Collation}" : "")} USING {col}::{to.Type};");
        if (from.Identity != to.Identity)
        {
            if (to.Identity == null) list.Add($"ALTER TABLE {table} ALTER COLUMN {col} DROP IDENTITY;");
            else if (from.Identity == null) list.Add($"ALTER TABLE {table} ALTER COLUMN {col} ADD GENERATED {(to.Identity == "always" ? "ALWAYS" : "BY DEFAULT")} AS IDENTITY;");
            else list.Add($"ALTER TABLE {table} ALTER COLUMN {col} SET GENERATED {(to.Identity == "always" ? "ALWAYS" : "BY DEFAULT")};");
        }
        if (from.Nullable != to.Nullable) list.Add($"ALTER TABLE {table} ALTER COLUMN {col} {(to.Nullable ? "DROP" : "SET")} NOT NULL;");
        return list.ToArray();
    }

    public string? AlterColumnRefusal(ModelColumn from, ModelColumn to) => null;
    public string? DropDefault(ModelTable t, ModelColumn c) => c.Default == null ? null : $"ALTER TABLE {N(t.Schema, t.Name)} ALTER COLUMN {Q(c.Name)} DROP DEFAULT;";
    public string? AddDefault(ModelTable t, ModelColumn c) => c.Default == null ? null : $"ALTER TABLE {N(t.Schema, t.Name)} ALTER COLUMN {Q(c.Name)} SET DEFAULT {c.Default};";
    public string AddKey(ModelTable t, ModelKey k, bool primary) => $"ALTER TABLE {N(t.Schema, t.Name)} ADD {KeyDef(k, primary)};";
    public string DropConstraint(ModelTable t, string name) => $"ALTER TABLE {N(t.Schema, t.Name)} DROP CONSTRAINT {Q(name)};";
    public string AddCheck(ModelTable t, ModelCheck c) => $"ALTER TABLE {N(t.Schema, t.Name)} ADD CONSTRAINT {Q(c.Name)} {c.Expression};";

    public string CreateIndex(ModelTable t, ModelIndex i) => (i.Definition ?? throw new InvalidOperationException($"Index {i.Name} has no definition.")).TrimEnd(';') + ";";
    public string DropIndex(ModelTable t, ModelIndex i) => $"DROP INDEX {N(t.Schema, i.Name)};";

    public string AddFk(ModelTable t, ModelFk f) =>
        $"ALTER TABLE {N(t.Schema, t.Name)} ADD CONSTRAINT {Q(f.Name)} FOREIGN KEY ({string.Join(", ", f.Columns.Select(Q))}) " +
        $"REFERENCES {N(f.RefSchema, f.RefTable)} ({string.Join(", ", f.RefColumns.Select(Q))}) ON DELETE {f.OnDelete} ON UPDATE {f.OnUpdate}{(f.Deferrable ? " DEFERRABLE" : "")};";

    public string CreateModule(ModelModule m, int targetMajor)
    {
        var def = (m.Definition ?? throw new InvalidOperationException($"{m.Schema}.{m.Name} has no definition.")).Trim();
        if (m.Kind == ModuleKind.Trigger && targetMajor >= 14)
        {
            // CREATE OR REPLACE TRIGGER (PostgreSQL 14+), on the token stream.
            var first = PgTokenizer.Tokenize(def).FirstOrDefault(t => !t.IsTrivia);
            if (first.Is("CREATE")) def = def[..(first.Offset + first.Length)] + " OR REPLACE" + def[(first.Offset + first.Length)..];
        }
        return def;
    }

    /// <summary>
    /// CREATE OR REPLACE cannot change a routine's result type, drop or retype a view's columns, or replace a materialized
    /// view (or a trigger before 14): those are dropped and created.
    /// </summary>
    public bool NeedsDropBeforeCreate(ModelModule? existing, ModelModule wanted, int targetMajor)
    {
        if (existing == null) return false;
        if (existing.Kind != wanted.Kind) return true;
        return wanted.Kind switch
        {
            ModuleKind.MaterializedView => true,
            ModuleKind.Trigger => targetMajor < 14,
            ModuleKind.Function or ModuleKind.Procedure => existing.Returns != wanted.Returns,
            ModuleKind.View => !ViewColumnsAppendOnly(existing.Columns ?? [], wanted.Columns ?? []),
            _ => false,
        };
    }

    static bool ViewColumnsAppendOnly(string[] existing, string[] wanted) =>
        wanted.Length >= existing.Length && existing.Select((c, i) => c == wanted[i]).All(x => x);

    public bool DependencyBlocks(ModelModule dependent) => true;

    public string DropModule(ModelModule m) => m.Kind switch
    {
        ModuleKind.View => $"DROP VIEW {N(m.Schema, m.Name)};",
        ModuleKind.MaterializedView => $"DROP MATERIALIZED VIEW {N(m.Schema, m.Name)};",
        ModuleKind.Procedure => $"DROP PROCEDURE {N(m.Schema, m.Name)}({m.Signature});",
        ModuleKind.Trigger => $"DROP TRIGGER {Q(m.Name)} ON {N(m.Schema, m.Table!)};",
        _ => $"DROP FUNCTION {N(m.Schema, m.Name)}({m.Signature});",
    };

    /// <summary>Views store parsed queries and follow table changes themselves.</summary>
    public string? RefreshModule(ModelModule m) => null;

    public string RenameObject(RefKind kind, string schema, string name, string newName)
    {
        var what = kind switch
        {
            RefKind.View => "VIEW", RefKind.Function => "FUNCTION", RefKind.Procedure => "PROCEDURE", RefKind.Type => "TYPE", RefKind.Sequence => "SEQUENCE", _ => "TABLE",
        };
        return $"ALTER {what} {N(schema, name)} RENAME TO {Q(newName)};";
    }

    public string RenameIdentifier(string expression, string from, string to)
    {
        var sb = new StringBuilder(expression.Length + 8);
        foreach (var t in PgTokenizer.Tokenize(expression))
        {
            var name = t.Kind switch
            {
                PgTokenKind.Word => t.Text.ToLowerInvariant(),
                PgTokenKind.QuotedIdent when t.Text.Length >= 2 => t.Text[1..^1].Replace("\"\"", "\""),
                _ => null,
            };
            sb.Append(name == from ? (t.Kind == PgTokenKind.Word && PlainName(to) ? to : Q(to)) : t.Text);
        }
        return sb.ToString();
    }

    public string Wrap(IReadOnlyList<MigrationStep> steps, string header)
    {
        var sb = new StringBuilder(header.TrimEnd()).Append("\n\n");
        var before = steps.Where(s => s.Phase == MigrationPhase.BeforeTransaction).ToList();
        if (before.Count > 0)
        {
            sb.Append("-- Runs before the transaction: an enum value added inside a transaction cannot be used in it.\n");
            sb.Append("-- ADD VALUE IF NOT EXISTS only adds, so running it again is harmless.\n");
            foreach (var s in before) sb.Append("-- ").Append(CommentText.Safe(s.Comment)).Append('\n').Append(s.Sql.TrimEnd()).Append('\n');
            sb.Append('\n');
        }
        sb.Append("BEGIN;\n\n");
        foreach (var s in steps.Where(s => s.Phase != MigrationPhase.BeforeTransaction))
            sb.Append("-- ").Append(CommentText.Safe(s.Comment)).Append('\n').Append(s.Sql.TrimEnd()).Append("\n\n");
        sb.Append("COMMIT;\n");
        return sb.ToString();
    }
}
