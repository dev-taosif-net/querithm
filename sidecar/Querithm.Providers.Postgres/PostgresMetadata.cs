using System.Data.Common;
using System.Text;
using Npgsql;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;

namespace Querithm.Providers.Postgres;

/// <summary>Catalog metadata for PostgreSQL (§8). xmin is the change marker since PostgreSQL has no modify date.</summary>
public sealed class PostgresMetadata : IMetadataProvider
{
    const string SystemSchemas = "n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg\\_toast%' AND n.nspname NOT LIKE 'pg\\_temp%'";

    /// <summary>
    /// A user trigger on a partitioned table is cloned onto every partition; the clones are not internal from
    /// PostgreSQL 13 and depend on their parent trigger (pg_depend, every version — tgparentid only exists from 13).
    /// Only the parent can be dropped or replaced, so the clones stay out of the snapshot. Expects pg_trigger as t.
    /// </summary>
    internal const string NotPartitionClone =
        "NOT EXISTS (SELECT 1 FROM pg_depend pd WHERE pd.classid = 'pg_trigger'::regclass AND pd.objid = t.oid AND pd.refclassid = 'pg_trigger'::regclass)";

    public async Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        // Marker = (pg_class.xmin << 32) | max(pg_attribute.xmin): column changes do not always touch pg_class.
        // Triggers (user-defined, not the internal FK ones): (pg_trigger.xmin << 32) | the trigger function's xmin, so
        // CREATE OR REPLACE TRIGGER and a replaced function both count as a change. The function itself stays a Func.
        cmd.CommandText = $"""
            SELECT c.oid::bigint, n.nspname, c.relname, c.relkind::text,
                   (c.xmin::text::bigint << 32) | COALESCE((SELECT max(a.xmin::text::bigint) FROM pg_attribute a WHERE a.attrelid = c.oid), 0),
                   NULL::text, NULL::bigint
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r','p','v','m','f','S') AND {SystemSchemas}
            UNION ALL
            SELECT p.oid::bigint, n.nspname, p.proname,
                   CASE WHEN p.prokind = 'p' THEN 'P' WHEN p.proretset THEN 'T' ELSE 'F' END,
                   p.xmin::text::bigint, pg_get_function_result(p.oid), NULL::bigint
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            LEFT JOIN pg_depend d ON d.objid = p.oid AND d.deptype = 'e'
            WHERE p.prokind IN ('f','p') AND {SystemSchemas} AND d.objid IS NULL
            UNION ALL
            SELECT t.oid::bigint, n.nspname, t.tgname, 'G',
                   (t.xmin::text::bigint << 32) | f.xmin::text::bigint, NULL::text, t.tgrelid::bigint
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_proc f ON f.oid = t.tgfoid
            WHERE NOT t.tgisinternal AND {NotPartitionClone} AND c.relkind IN ('r','p','v','m','f') AND {SystemSchemas}
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ObjectStamp>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var kind = r.GetString(3) switch
            {
                "r" or "p" or "f" => ObjectKind.Table, "v" or "m" => ObjectKind.View, "S" => ObjectKind.Sequence,
                "P" => ObjectKind.Proc, "T" => ObjectKind.Tvf, "G" => ObjectKind.Trigger, _ => ObjectKind.Func,
            };
            list.Add(new ObjectStamp(r.GetInt64(0), r.GetString(1), r.GetString(2), kind, r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6)));
        }
        return list;
    }

    public async Task<MetadataBundle> GetDetailsAsync(DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var columns = new List<ColumnRow>();
        var parms = new List<ParamRow>();
        var indexes = new List<IndexRow>();
        var fks = new List<FkRow>();
        foreach (var chunk in objectIds.Chunk(500))
        {
            var ids = chunk.ToArray();
            await using (var cmd = (NpgsqlCommand)c.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT a.attrelid::bigint, a.attnum, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
                           a.attidentity <> '' OR COALESCE(pg_get_expr(ad.adbin, ad.adrelid), '') LIKE 'nextval(%',
                           a.attgenerated <> '', pg_get_expr(ad.adbin, ad.adrelid)
                    FROM pg_attribute a LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
                    WHERE a.attrelid = ANY(@ids::oid[]) AND a.attnum > 0 AND NOT a.attisdropped
                    ORDER BY a.attrelid, a.attnum
                    """;
                cmd.Parameters.AddWithValue("ids", ids);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    columns.Add(new ColumnRow(r.GetInt64(0), r.GetInt16(1), r.GetString(2), r.GetString(3), null, null, null,
                        r.GetBoolean(4), r.GetBoolean(5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetString(7)));
            }
            await using (var cmd = (NpgsqlCommand)c.CreateCommand())
            {
                // Arguments of functions/procedures; OUT/TABLE arguments of set-returning functions become columns.
                cmd.CommandText = """
                    SELECT p.oid::bigint, a.ord::int, COALESCE(a.name, ''), format_type(a.typ, NULL), COALESCE(a.mode::text, 'i'),
                           (a.ord > p.pronargs - p.pronargdefaults) AND COALESCE(a.mode::text, 'i') IN ('i','b','v'), p.proretset
                    FROM pg_proc p,
                         LATERAL unnest(COALESCE(p.proallargtypes, p.proargtypes::oid[]), p.proargnames, p.proargmodes)
                                 WITH ORDINALITY AS a(typ, name, mode, ord)
                    WHERE p.oid = ANY(@ids::oid[])
                    ORDER BY p.oid, a.ord
                    """;
                cmd.Parameters.AddWithValue("ids", ids);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var inputOrdinal = new Dictionary<long, int>();
                var colOrdinal = new Dictionary<long, int>();
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var oid = r.GetInt64(0);
                    var mode = r.GetString(4);
                    if (mode is "t" || (mode is "o" && r.GetBoolean(6)))
                    {
                        colOrdinal[oid] = colOrdinal.GetValueOrDefault(oid) + 1;
                        columns.Add(new ColumnRow(oid, colOrdinal[oid], r.GetString(2), r.GetString(3), null, null, null, true, false, false, null));
                        continue;
                    }
                    inputOrdinal[oid] = inputOrdinal.GetValueOrDefault(oid) + 1;
                    parms.Add(new ParamRow(oid, inputOrdinal[oid], r.GetString(2), r.GetString(3), null, mode is "o" or "b", r.GetBoolean(5)));
                }
            }
            await using (var cmd = (NpgsqlCommand)c.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT i.indrelid::bigint, ic.relname, i.indisunique, i.indisprimary,
                           ARRAY(SELECT a.attname FROM unnest(i.indkey) WITH ORDINALITY k(attnum, ord)
                                 JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum ORDER BY k.ord)::text[]
                    FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid
                    WHERE i.indrelid = ANY(@ids::oid[])
                    """;
                cmd.Parameters.AddWithValue("ids", ids);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    indexes.Add(new IndexRow(r.GetInt64(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetFieldValue<string[]>(4)));
            }
            await using (var cmd = (NpgsqlCommand)c.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT con.conname, con.conrelid::bigint, con.confrelid::bigint,
                           ARRAY(SELECT a.attname FROM unnest(con.conkey) WITH ORDINALITY k(n, o) JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.n ORDER BY k.o)::text[],
                           ARRAY(SELECT a.attname FROM unnest(con.confkey) WITH ORDINALITY k(n, o) JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.n ORDER BY k.o)::text[]
                    FROM pg_constraint con WHERE con.contype = 'f' AND con.conrelid = ANY(@ids::oid[])
                    """;
                cmd.Parameters.AddWithValue("ids", ids);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var pc = r.GetFieldValue<string[]>(3);
                    var rc = r.GetFieldValue<string[]>(4);
                    fks.Add(new FkRow($"{r.GetString(0)}@{r.GetInt64(1)}", r.GetInt64(1), r.GetInt64(2),
                        pc.Zip(rc, (a, b) => new FkColumnPair { Parent = a, Referenced = b }).ToArray()));
                }
            }
        }
        return new MetadataBundle(columns, parms, indexes, fks);
    }

    static string QuoteIdent(string s) => s.All(ch => char.IsLower(ch) || char.IsDigit(ch) || ch == '_') && s.Length > 0 && !char.IsDigit(s[0])
        ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    public async Task<string?> GetDefinitionAsync(DbConnection c, ObjectRef obj, CancellationToken ct)
    {
        await using var cmd = (NpgsqlCommand)c.CreateCommand();
        if (obj.Kind is ObjectKind.Proc or ObjectKind.Func or ObjectKind.Tvf)
        {
            cmd.CommandText = obj.ObjectId is { } id
                ? "SELECT pg_get_functiondef(@oid::oid)"
                : "SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = @s AND p.proname = @n LIMIT 1";
        }
        else if (obj.Kind == ObjectKind.Trigger)
        {
            // A trigger name is unique per table only; without an oid the first one in the schema is taken.
            cmd.CommandText = $"""
                SELECT pg_get_triggerdef(t.oid, true) || ';'
                FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE NOT t.tgisinternal AND {NotPartitionClone} AND ((@oid::bigint IS NOT NULL AND t.oid = @oid::oid) OR (@oid::bigint IS NULL AND n.nspname = @s AND t.tgname = @n))
                ORDER BY t.oid LIMIT 1
                """;
        }
        else if (obj.Kind == ObjectKind.View)
        {
            cmd.CommandText = """
                SELECT CASE c.relkind WHEN 'm' THEN 'CREATE MATERIALIZED VIEW ' ELSE 'CREATE OR REPLACE VIEW ' END
                       || quote_ident(n.nspname) || '.' || quote_ident(c.relname) || E' AS\n' || pg_get_viewdef(c.oid, true)
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE (@oid::bigint IS NOT NULL AND c.oid = @oid::oid) OR (@oid::bigint IS NULL AND n.nspname = @s AND c.relname = @n)
                """;
        }
        else return null;
        cmd.Parameters.AddWithValue("oid", obj.ObjectId is { } o ? o : DBNull.Value).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint;
        cmd.Parameters.AddWithValue("s", obj.Schema);
        cmd.Parameters.AddWithValue("n", obj.Name);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var list = new List<(long, string?)>();
        foreach (var chunk in objectIds.Chunk(500))
        {
            await using var cmd = (NpgsqlCommand)c.CreateCommand();
            cmd.CommandText = """
                SELECT p.oid::bigint, pg_get_functiondef(p.oid) FROM pg_proc p WHERE p.oid = ANY(@ids::oid[]) AND p.prokind IN ('f','p')
                UNION ALL
                SELECT c.oid::bigint, CASE c.relkind WHEN 'm' THEN 'CREATE MATERIALIZED VIEW ' ELSE 'CREATE OR REPLACE VIEW ' END
                       || quote_ident(n.nspname) || '.' || quote_ident(c.relname) || E' AS\n' || pg_get_viewdef(c.oid, true)
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.oid = ANY(@ids::oid[]) AND c.relkind IN ('v','m')
                UNION ALL
                SELECT t.oid::bigint, pg_get_triggerdef(t.oid, true) || ';' FROM pg_trigger t WHERE t.oid = ANY(@ids::oid[]) AND NOT t.tgisinternal
                """;
            cmd.Parameters.AddWithValue("ids", chunk.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }
        return list;
    }

    public async Task<Dependency[]> GetDependenciesAsync(DbConnection c, ObjectRef obj, CancellationToken ct)
    {
        await using var cmd = (NpgsqlCommand)c.CreateCommand();
        // Views record their dependencies through pg_rewrite; tables are "used by" those views (§10 Dependencies).
        cmd.CommandText = $"""
            WITH target AS (
              SELECT COALESCE(@oid::oid, (SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @s AND c.relname = @n)) AS oid
            )
            SELECT DISTINCT 'uses', n.nspname, c.relname, c.relkind::text, c.oid::bigint
            FROM pg_rewrite r JOIN pg_depend d ON d.objid = r.oid AND d.classid = 'pg_rewrite'::regclass
            JOIN pg_class c ON c.oid = d.refobjid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE r.ev_class = (SELECT oid FROM target) AND d.refobjid <> r.ev_class
            UNION
            SELECT DISTINCT 'usedBy', n.nspname, c.relname, c.relkind::text, c.oid::bigint
            FROM pg_depend d JOIN pg_rewrite r ON r.oid = d.objid AND d.classid = 'pg_rewrite'::regclass
            JOIN pg_class c ON c.oid = r.ev_class JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE d.refobjid = (SELECT oid FROM target) AND r.ev_class <> d.refobjid
            UNION
            SELECT DISTINCT 'usedBy', n.nspname, p.proname, 'F', p.oid::bigint
            FROM pg_depend d JOIN pg_proc p ON p.oid = d.objid AND d.classid = 'pg_proc'::regclass
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE d.refobjid = (SELECT oid FROM target) AND d.deptype = 'n'
            UNION
            SELECT 'uses', n.nspname, f.proname, 'F', f.oid::bigint
            FROM pg_trigger t JOIN pg_proc f ON f.oid = t.tgfoid JOIN pg_namespace n ON n.oid = f.pronamespace
            WHERE t.oid = (SELECT oid FROM target)
            UNION
            SELECT 'uses', n.nspname, c.relname, c.relkind::text, c.oid::bigint
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE t.oid = (SELECT oid FROM target)
            UNION
            SELECT 'usedBy', n.nspname, t.tgname, 'G', t.oid::bigint
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE t.tgrelid = (SELECT oid FROM target) AND NOT t.tgisinternal AND {NotPartitionClone}
            """;
        cmd.Parameters.AddWithValue("oid", obj.ObjectId is { } o ? (uint)o : DBNull.Value).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Oid;
        cmd.Parameters.AddWithValue("s", obj.Schema);
        cmd.Parameters.AddWithValue("n", obj.Name);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Dependency>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var kind = r.GetString(3) switch { "v" or "m" => ObjectKind.View, "F" => ObjectKind.Func, "S" => ObjectKind.Sequence, "G" => ObjectKind.Trigger, _ => ObjectKind.Table };
            list.Add(new Dependency
            {
                Direction = r.GetString(0) == "uses" ? DependencyDirection.Uses : DependencyDirection.UsedBy,
                Object = new ObjectRef { Schema = r.GetString(1), Name = r.GetString(2), Kind = kind, ObjectId = r.GetInt64(4) },
            });
        }
        return list.ToArray();
    }

    /// <summary>
    /// pg_get_functiondef already emits CREATE OR REPLACE; plain CREATE gets OR REPLACE inserted on the token stream
    /// (CREATE TRIGGER only from PostgreSQL 14, which has CREATE OR REPLACE TRIGGER). CREATE CONSTRAINT TRIGGER stays
    /// as is: PostgreSQL rejects OR REPLACE for constraint triggers.
    /// </summary>
    public string ToEditableScript(string definition, Capabilities caps)
    {
        var tokens = PgTokenizer.Tokenize(definition).Where(t => !t.IsTrivia).Take(3).ToList();
        if (tokens.Count >= 2 && tokens[0].Is("CREATE") && !tokens[1].Is("OR") &&
            (tokens[1].Is("FUNCTION") || tokens[1].Is("PROCEDURE") || tokens[1].Is("VIEW") || (tokens[1].Is("TRIGGER") && caps.MajorVersion >= 14)))
        {
            var t = tokens[0];
            return new StringBuilder(definition).Insert(t.Offset + t.Length, " OR REPLACE").ToString();
        }
        return definition;
    }

    public static string Qualified(ObjectRef o) => $"{QuoteIdent(o.Schema)}.{QuoteIdent(o.Name)}";

    /// <summary>pg_class.reltuples (planner estimate; -1 = never analyzed, reported as unknown).</summary>
    public async Task<RowCount[]> GetRowCountsAsync(DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var list = new List<RowCount>();
        foreach (var chunk in objectIds.Chunk(500))
        {
            await using var cmd = (NpgsqlCommand)c.CreateCommand();
            cmd.CommandText = "SELECT c.oid::bigint, CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END FROM pg_class c WHERE c.oid = ANY(@ids::oid[])";
            cmd.Parameters.AddWithValue("ids", chunk.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add(new RowCount { ObjectId = r.GetInt64(0), Rows = r.IsDBNull(1) ? null : r.GetInt64(1), Estimated = true });
        }
        return list.ToArray();
    }
}
