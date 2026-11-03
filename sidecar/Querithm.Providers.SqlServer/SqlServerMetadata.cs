using System.Data.Common;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql;

namespace Querithm.Providers.SqlServer;

/// <summary>Catalog-view metadata for SQL Server (§8 Metadata queries).</summary>
public sealed class SqlServerMetadata : IMetadataProvider
{
    public async Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        // DML triggers (TR, CLR TA) are schema-scoped objects with a parent table or view; database DDL triggers are not
        // in sys.objects and stay out of the snapshot.
        cmd.CommandText = """
            SELECT o.object_id, s.name, o.name, RTRIM(o.type), DATEDIFF_BIG(MILLISECOND, '19700101', o.modify_date), rt.name, NULLIF(o.parent_object_id, 0)
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.parameters rp ON rp.object_id = o.object_id AND rp.parameter_id = 0 AND o.type IN ('FN','FS')
            LEFT JOIN sys.types rt ON rt.user_type_id = rp.user_type_id
            WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V','P','PC','X','FN','FS','IF','TF','FT','SO','SN','TR','TA')
            UNION ALL
            SELECT tt.type_table_object_id, s.name, tt.name, 'TT', DATEDIFF_BIG(MILLISECOND, '19700101', o.modify_date), NULL, NULL
            FROM sys.table_types tt
            JOIN sys.schemas s ON s.schema_id = tt.schema_id
            JOIN sys.objects o ON o.object_id = tt.type_table_object_id
            WHERE tt.is_user_defined = 1
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ObjectStamp>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var kind = r.GetString(3) switch
            {
                "U" => ObjectKind.Table, "V" => ObjectKind.View, "P" or "PC" or "X" => ObjectKind.Proc, "FN" or "FS" => ObjectKind.Func,
                "IF" or "TF" or "FT" => ObjectKind.Tvf, "SO" => ObjectKind.Sequence, "SN" => ObjectKind.Synonym, "TR" or "TA" => ObjectKind.Trigger, _ => ObjectKind.Type,
            };
            list.Add(new ObjectStamp(r.GetInt32(0), r.GetString(1), r.GetString(2), kind, r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt32(6)));
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
            // Object ids are integers from the catalog, so the IN list is built from numbers only.
            var ids = string.Join(",", chunk);
            await using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT c.object_id, c.column_id, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed,
                           dc.definition, t.is_user_defined, bt.name
                    FROM sys.columns c
                    JOIN sys.types t ON t.user_type_id = c.user_type_id
                    LEFT JOIN sys.types bt ON bt.user_type_id = t.system_type_id AND t.is_user_defined = 1
                    LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
                    WHERE c.object_id IN ({ids})
                    ORDER BY c.object_id, c.column_id
                    """;
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var type = r.GetString(3);
                    int maxLen = r.GetInt16(4), precision = r.GetByte(5), scale = r.GetByte(6);
                    columns.Add(new ColumnRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), TypeDisplay(type, maxLen, precision, scale),
                        maxLen, precision, scale, r.GetBoolean(7), r.GetBoolean(8), r.GetBoolean(9), r.IsDBNull(10) ? null : r.GetString(10)));
                }
            }
            await using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT p.object_id, p.parameter_id, p.name, t.name, p.max_length, p.precision, p.scale, p.is_output, p.has_default_value
                    FROM sys.parameters p JOIN sys.types t ON t.user_type_id = p.user_type_id
                    WHERE p.object_id IN ({ids}) AND p.parameter_id > 0
                    ORDER BY p.object_id, p.parameter_id
                    """;
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    parms.Add(new ParamRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), TypeDisplay(r.GetString(3), r.GetInt16(4), r.GetByte(5), r.GetByte(6)),
                        r.GetInt16(4), r.GetBoolean(7), r.GetBoolean(8)));
            }
            await using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT i.object_id, i.name, i.is_unique, i.is_primary_key,
                           STUFF((SELECT ',' + col.name FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                                  WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                                  ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, '')
                    FROM sys.indexes i
                    WHERE i.object_id IN ({ids}) AND i.name IS NOT NULL AND i.type > 0
                    """;
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    indexes.Add(new IndexRow(r.GetInt32(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3),
                        r.IsDBNull(4) ? [] : r.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries)));
            }
            await using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT fk.name, fk.parent_object_id, fk.referenced_object_id, pc.name, rc.name
                    FROM sys.foreign_keys fk
                    JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                    JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                    JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                    WHERE fk.parent_object_id IN ({ids})
                    ORDER BY fk.object_id, fkc.constraint_column_id
                    """;
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var byName = new Dictionary<string, (long P, long R, List<FkColumnPair> Cols)>();
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var name = r.GetString(0);
                    if (!byName.TryGetValue(name, out var fk)) byName[name] = fk = (r.GetInt32(1), r.GetInt32(2), []);
                    fk.Cols.Add(new FkColumnPair { Parent = r.GetString(3), Referenced = r.GetString(4) });
                }
                fks.AddRange(byName.Select(kv => new FkRow(kv.Key, kv.Value.P, kv.Value.R, kv.Value.Cols.ToArray())));
            }
        }
        return new MetadataBundle(columns, parms, indexes, fks);
    }

    public static string TypeDisplay(string type, int maxLen, int precision, int scale)
    {
        var t = type.ToLowerInvariant();
        return t switch
        {
            "nvarchar" or "nchar" => $"{t}({(maxLen == -1 ? "max" : (maxLen / 2).ToString())})",
            "varchar" or "char" or "varbinary" or "binary" => $"{t}({(maxLen == -1 ? "max" : maxLen.ToString())})",
            "decimal" or "numeric" => $"{t}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => scale == 7 ? t : $"{t}({scale})",
            "float" => precision == 53 ? t : $"float({precision})",
            _ => t,
        };
    }

    static string ObjectNameSql(ObjectRef o) => $"{Quote(o.Schema)}.{Quote(o.Name)}";
    static string Quote(string s) => "[" + s.Replace("]", "]]") + "]";

    public async Task<string?> GetDefinitionAsync(DbConnection c, ObjectRef obj, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT OBJECT_DEFINITION(COALESCE(@id, OBJECT_ID(@name)))";
        cmd.Parameters.Add(Param(cmd, "@id", obj.ObjectId is { } id ? (int)id : DBNull.Value));
        cmd.Parameters.Add(Param(cmd, "@name", ObjectNameSql(obj)));
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is string s ? s : null;
    }

    public async Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var list = new List<(long, string?)>();
        foreach (var chunk in objectIds.Chunk(500))
        {
            await using var cmd = c.CreateCommand();
            // Ids are catalog integers; OBJECT_DEFINITION is NULL for an encrypted module.
            cmd.CommandText = $"SELECT o.object_id, OBJECT_DEFINITION(o.object_id) FROM sys.objects o WHERE o.object_id IN ({string.Join(",", chunk.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)))})";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add((r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }
        return list;
    }

    static DbParameter Param(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }

    public async Task<Dependency[]> GetDependenciesAsync(DbConnection c, ObjectRef obj, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DECLARE @oid int = COALESCE(@id, OBJECT_ID(@name));
            SELECT 'uses', COALESCE(rs.name, d.referenced_schema_name, 'dbo'), d.referenced_entity_name, RTRIM(ro.type), d.referenced_id, d.referenced_database_name
            FROM sys.sql_expression_dependencies d
            LEFT JOIN sys.objects ro ON ro.object_id = d.referenced_id
            LEFT JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
            WHERE d.referencing_id = @oid
            UNION
            SELECT 'usedBy', s.name, o.name, RTRIM(o.type), o.object_id, NULL
            FROM sys.sql_expression_dependencies d
            JOIN sys.objects o ON o.object_id = d.referencing_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE d.referenced_id = @oid
            """;
        cmd.Parameters.Add(Param(cmd, "@id", obj.ObjectId is { } id ? (int)id : DBNull.Value));
        cmd.Parameters.Add(Param(cmd, "@name", ObjectNameSql(obj)));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Dependency>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var kind = r.IsDBNull(3) ? ObjectKind.Table : r.GetString(3) switch
            {
                "V" => ObjectKind.View, "P" or "PC" or "X" => ObjectKind.Proc, "FN" or "FS" => ObjectKind.Func, "IF" or "TF" or "FT" => ObjectKind.Tvf,
                "SO" => ObjectKind.Sequence, "SN" => ObjectKind.Synonym, "TR" or "TA" => ObjectKind.Trigger, _ => ObjectKind.Table,
            };
            list.Add(new Dependency
            {
                Direction = r.GetString(0) == "uses" ? DependencyDirection.Uses : DependencyDirection.UsedBy,
                Object = new ObjectRef
                {
                    Schema = r.GetString(1), Name = r.GetString(2), Kind = kind, ObjectId = r.IsDBNull(4) ? null : r.GetInt32(4),
                    Database = r.IsDBNull(5) ? null : r.GetString(5),
                },
            });
        }
        return list.ToArray();
    }

    /// <summary>CREATE → CREATE OR ALTER (2016 SP1+) or ALTER, found on the token stream (§15 rule 6).</summary>
    public string ToEditableScript(string definition, Capabilities caps)
    {
        var tokens = TSqlParsing.NewParser().GetTokenStream(new StringReader(definition), out _);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment) continue;
            if (t.TokenType != TSqlTokenType.Create) return definition; // already ALTER, or not a module
            // Already CREATE OR ALTER?
            var j = i + 1;
            while (j < tokens.Count && tokens[j].TokenType == TSqlTokenType.WhiteSpace) j++;
            if (j < tokens.Count && tokens[j].TokenType == TSqlTokenType.Or) return definition;
            var sb = new StringBuilder(definition.Length + 10);
            sb.Append(definition, 0, t.Offset);
            sb.Append(caps.CreateOrAlter ? "CREATE OR ALTER" : "ALTER");
            sb.Append(definition, t.Offset + t.Text.Length, definition.Length - t.Offset - t.Text.Length);
            return sb.ToString();
        }
        return definition;
    }

    /// <summary>sys.dm_db_partition_stats (committed counts; needs VIEW DATABASE STATE), else the approximate sys.partitions.</summary>
    public async Task<RowCount[]> GetRowCountsAsync(DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var list = new List<RowCount>();
        var exact = true;
        foreach (var chunk in objectIds.Chunk(500))
        {
            var ids = string.Join(",", chunk.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            async Task Read(string sql, bool estimated)
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    list.Add(new RowCount { ObjectId = r.GetInt32(0), Rows = r.IsDBNull(1) ? null : r.GetInt64(1), Estimated = estimated });
            }
            if (exact)
            {
                try
                {
                    await Read($"SELECT object_id, SUM(row_count) FROM sys.dm_db_partition_stats WHERE index_id IN (0,1) AND object_id IN ({ids}) GROUP BY object_id", false);
                    continue;
                }
                catch (DbException) { exact = false; } // no VIEW DATABASE STATE
            }
            await Read($"SELECT object_id, SUM(CAST(rows AS bigint)) FROM sys.partitions WHERE index_id IN (0,1) AND object_id IN ({ids}) GROUP BY object_id", true);
        }
        return list.ToArray();
    }
}
