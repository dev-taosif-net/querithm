using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Data.SqlClient;
using Querithm.Contract;
using Querithm.Contract.Providers;
using TypeSpec = Querithm.Providers.SqlServer.SqlServerDataEdit.TypeSpec;

namespace Querithm.Providers.SqlServer;

/// <summary>
/// Importing files into SQL Server: types for new tables, the compatibility check, DDL, and SqlBulkCopy with
/// streaming, name-based column mappings and per-row conversion (a value that does not convert rejects its row only).
/// </summary>
public sealed class SqlServerImport : IImportDialect
{
    static string Q(string s) => "[" + s.Replace("]", "]]") + "]";

    static readonly int[] TextSizes = [50, 100, 255, 500, 1000, 4000];

    static string TextType(int maxLen, string name = "nvarchar")
    {
        var limit = name is "varchar" or "char" ? 8000 : 4000;
        foreach (var n in TextSizes) if (maxLen <= n && n <= limit) return $"{name}({n})";
        return maxLen <= limit ? $"{name}({limit})" : $"{name}(max)";
    }

    public string TypeFor(ImportColumnProfile c) => c.Kind switch
    {
        ImportKind.Bool => "bit",
        ImportKind.Int32 => "int",
        ImportKind.Int64 => "bigint",
        ImportKind.Decimal => $"decimal({Math.Clamp(c.Precision, 1, 38)},{Math.Clamp(c.Scale, 0, 38)})",
        ImportKind.Double => "float",
        ImportKind.Date => "date",
        ImportKind.DateTime => $"datetime2({Math.Min(7, c.Precision)})",
        ImportKind.DateTimeOffset => $"datetimeoffset({Math.Min(7, c.Precision)})",
        ImportKind.Time => $"time({Math.Min(7, c.Precision)})",
        ImportKind.Uuid => "uniqueidentifier",
        _ => TextType(Math.Max(1, c.MaxLength)),
    };

    public bool NullUsesDefault => true;

    public bool BlankIsNull(string? type) =>
        type != null && TypeSpec.Parse(type).Name is not ("char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" or "sql_variant" or "sysname");

    static int Digits(string? v) => v == null ? 0 : v.TrimStart('-', '+').TrimStart('0').Length;

    static (BigInteger Min, BigInteger Max)? IntRange(string name) => name switch
    {
        "tinyint" => (0, 255),
        "smallint" => (short.MinValue, short.MaxValue),
        "int" => (int.MinValue, int.MaxValue),
        "bigint" => (long.MinValue, long.MaxValue),
        _ => null,
    };

    public ImportFit Check(ImportColumnProfile c, SnapColumn target)
    {
        var t = TypeSpec.Parse(target.Type);
        var type = target.Type;
        if (c.Kind == ImportKind.Empty) return new ImportFit(ImportVerdictKind.Fits, "Every value is NULL");
        string Fits() => $"Fits {type}";
        switch (t.Name)
        {
            case "char" or "varchar" or "nchar" or "nvarchar":
                if (t.Max || c.MaxLength <= (t.A ?? 1)) return new ImportFit(ImportVerdictKind.Fits, Fits());
                return new ImportFit(ImportVerdictKind.Widen, $"Values are up to {c.MaxLength:N0} characters; {type} holds {t.A}", TextType(c.MaxLength, t.Name is "char" ? "varchar" : t.Name is "nchar" ? "nvarchar" : t.Name));
            case "text" or "ntext" or "xml" or "sql_variant":
                return new ImportFit(ImportVerdictKind.Fits, Fits());
            case "bit":
                if (c.Kind == ImportKind.Bool || c.Kind is ImportKind.Int32 && c.Min is "0" or "1" && c.Max is "0" or "1") return new ImportFit(ImportVerdictKind.Fits, Fits());
                return new ImportFit(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not true/false or 1/0: those rows will be rejected");
            case "tinyint" or "smallint" or "int" or "bigint":
            {
                if (c.Kind is not (ImportKind.Int32 or ImportKind.Int64))
                    return c.Kind == ImportKind.Decimal && c.Scale == 0 && c.Precision <= 38
                        ? new ImportFit(ImportVerdictKind.Widen, $"Whole numbers up to {c.Precision} digits do not fit {type}", $"decimal({c.Precision},0)")
                        : new ImportFit(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not whole numbers: those rows will be rejected");
                var (min, max) = IntRange(t.Name)!.Value;
                var lo = BigInteger.Parse(c.Min ?? "0", CultureInfo.InvariantCulture);
                var hi = BigInteger.Parse(c.Max ?? "0", CultureInfo.InvariantCulture);
                if (lo >= min && hi <= max) return new ImportFit(ImportVerdictKind.Fits, Fits());
                var wider = new[] { "smallint", "int", "bigint" }.FirstOrDefault(w => IntRange(w)!.Value is var r && lo >= r.Min && hi <= r.Max && Rank(w) > Rank(t.Name)) ?? "decimal(38,0)";
                return new ImportFit(ImportVerdictKind.Widen, $"Values range {c.Min}…{c.Max}, outside {type}", wider);
            }
            case "decimal" or "numeric" or "money" or "smallmoney":
            {
                var (p, s) = t.Name switch { "money" => (19, 4), "smallmoney" => (10, 4), _ => (t.A ?? 18, t.B ?? 0) };
                var (intDigits, scale) = c.Kind switch
                {
                    ImportKind.Int32 or ImportKind.Int64 => (Math.Max(Digits(c.Min), Digits(c.Max)), 0),
                    ImportKind.Decimal => (c.Precision - c.Scale, c.Scale),
                    ImportKind.Double => (-1, -1),
                    _ => (-2, -2),
                };
                if (intDigits == -2) return new ImportFit(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not numbers: those rows will be rejected");
                if (intDigits == -1) return new ImportFit(ImportVerdictKind.Widen, "Values use exponent notation or exceed 38 digits", "float");
                if (intDigits <= p - s && scale <= s) return new ImportFit(ImportVerdictKind.Fits, Fits());
                var ns = Math.Max(s, scale);
                var np = Math.Min(38, Math.Max(p - s, intDigits) + ns);
                return new ImportFit(ImportVerdictKind.Widen, $"Values need {intDigits} digit(s) before and {scale} after the point; {type} allows {p - s} and {s}", $"decimal({np},{ns})");
            }
            case "float" or "real":
                return c.Kind is ImportKind.Int32 or ImportKind.Int64 or ImportKind.Decimal or ImportKind.Double
                    ? new ImportFit(ImportVerdictKind.Fits, Fits())
                    : new ImportFit(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not numbers: those rows will be rejected");
            case "date":
                return c.Kind switch
                {
                    ImportKind.Date => new ImportFit(ImportVerdictKind.Fits, Fits()),
                    ImportKind.DateTime => new ImportFit(ImportVerdictKind.Widen, "Values have a time of day, which date would drop", "datetime2(" + Math.Min(7, c.Precision) + ")"),
                    _ => new ImportFit(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not dates (yyyy-mm-dd): those rows will be rejected"),
                };
            case "datetime" or "smalldatetime" or "datetime2":
            {
                if (c.Kind is not (ImportKind.Date or ImportKind.DateTime))
                    return new ImportFit(ImportVerdictKind.Mismatch, c.Kind == ImportKind.DateTimeOffset
                        ? $"Values carry a UTC offset, which {type} has no place for: those rows will be rejected"
                        : $"Values are {Describe(c)}, not dates and times: those rows will be rejected");
                var keeps = t.Name switch { "datetime" => 3, "smalldatetime" => 0, _ => t.TimeScale };
                return c.Precision <= keeps
                    ? new ImportFit(ImportVerdictKind.Fits, Fits())
                    : new ImportFit(ImportVerdictKind.Widen, $"Values have {c.Precision} fractional second digits; {type} keeps {keeps}", $"datetime2({Math.Min(7, c.Precision)})");
            }
            case "datetimeoffset":
                if (c.Kind is ImportKind.Date or ImportKind.DateTime)
                    return new ImportFit(ImportVerdictKind.Fits, $"Fits {type}; the file has no offsets, so +00:00 is stored");
                if (c.Kind != ImportKind.DateTimeOffset) return new ImportFit(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not dates and times: those rows will be rejected");
                return c.Precision <= t.TimeScale
                    ? new ImportFit(ImportVerdictKind.Fits, Fits())
                    : new ImportFit(ImportVerdictKind.Widen, $"Values have {c.Precision} fractional second digits; {type} keeps {t.TimeScale}", $"datetimeoffset({Math.Min(7, c.Precision)})");
            case "time":
                if (c.Kind != ImportKind.Time) return new ImportFit(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not times of day: those rows will be rejected");
                return c.Precision <= t.TimeScale
                    ? new ImportFit(ImportVerdictKind.Fits, Fits())
                    : new ImportFit(ImportVerdictKind.Widen, $"Values have {c.Precision} fractional second digits; {type} keeps {t.TimeScale}", $"time({Math.Min(7, c.Precision)})");
            case "uniqueidentifier":
                return c.Kind == ImportKind.Uuid
                    ? new ImportFit(ImportVerdictKind.Fits, Fits())
                    : new ImportFit(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not GUIDs: those rows will be rejected");
            default:
                return new ImportFit(ImportVerdictKind.Fits, $"{type}: the server converts the text when the rows are loaded");
        }
    }

    static int Rank(string n) => n switch { "tinyint" => 0, "smallint" => 1, "int" => 2, "bigint" => 3, _ => 4 };

    public static string Describe(ImportColumnProfile c) => c.Kind switch
    {
        ImportKind.Bool => "true/false",
        ImportKind.Int32 or ImportKind.Int64 => "whole numbers",
        ImportKind.Decimal => "decimals",
        ImportKind.Double => "numbers",
        ImportKind.Date => "dates",
        ImportKind.DateTime => "dates and times",
        ImportKind.DateTimeOffset => "dates and times with offsets",
        ImportKind.Time => "times of day",
        ImportKind.Uuid => "GUIDs",
        _ => "text",
    };

    public string CreateTable(string schema, string table, IReadOnlyList<(string Name, string Type, bool NotNull)> columns, IReadOnlyList<string> primaryKey)
    {
        var sb = new StringBuilder($"CREATE TABLE {Q(schema)}.{Q(table)} (\n");
        var lines = columns.Select(c => $"    {Q(c.Name)} {c.Type} {(c.NotNull || primaryKey.Contains(c.Name) ? "NOT NULL" : "NULL")}").ToList();
        if (primaryKey.Count > 0) lines.Add($"    CONSTRAINT {Q("PK_" + table)} PRIMARY KEY ({string.Join(", ", primaryKey.Select(Q))})");
        sb.Append(string.Join(",\n", lines)).Append("\n);");
        return sb.ToString();
    }

    public string AlterColumnType(string schema, string table, SnapColumn column, string newType) =>
        $"ALTER TABLE {Q(schema)}.{Q(table)} ALTER COLUMN {Q(column.Name)} {newType} {(column.Nullable ? "NULL" : "NOT NULL")};";

    public string AddColumn(string schema, string table, string column, string type) => $"ALTER TABLE {Q(schema)}.{Q(table)} ADD {Q(column)} {type} NULL;";

    public IBulkLoader? Loader { get; } = new SqlServerBulkLoader();
}

/// <summary>SqlBulkCopy: streaming, column mappings by name, the options the user picked.</summary>
public sealed class SqlServerBulkLoader : IBulkLoader
{
    /// <summary>SET IDENTITY_INSERT around INSERTs that write the identity column (KeepIdentity), or null.</summary>
    internal static (string On, string Off)? IdentityInsert(BulkTarget target, ImportRunOptions o)
    {
        if (!o.KeepIdentity || !target.Columns.Any(c => c.Identity)) return null;
        var table = $"[{target.Schema.Replace("]", "]]")}].[{target.Table.Replace("]", "]]")}]";
        return ($"SET IDENTITY_INSERT {table} ON;", $"SET IDENTITY_INSERT {table} OFF;");
    }

    static async Task RunAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task LoadAsync(DbConnection connection, BulkTarget target, IBulkRows rows, ImportRunOptions o, BulkTransaction tx, int? commandTimeoutS, CancellationToken ct)
    {
        var conn = (SqlConnection)connection;
        // Querithm keeps severity ≤ 16 errors as messages for SSMS-style batches; a load must fail on them instead.
        var asMessages = conn.FireInfoMessageEventOnUserErrors;
        conn.FireInfoMessageEventOnUserErrors = false;
        try
        {
            if (tx.Mode == BulkTxMode.Caller)
            {
                // SqlBulkCopy cannot join a transaction begun with T-SQL (the user's open transaction): multi-row INSERTs can.
                // They need IDENTITY_INSERT for the identity values KeepIdentity brings (bulk copy has its own option).
                var identity = IdentityInsert(target, o);
                if (identity is var (on, _)) await RunAsync(conn, on, ct).ConfigureAwait(false);
                try
                {
                    await new BatchInsertLoader(new SqlServerDataEdit(), new SqlServerTransactions()).LoadAsync(conn, target, rows, o, tx, commandTimeoutS, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (identity is var (_, off))
                        try { using var cleanup = Cleanup.Start(); await RunAsync(conn, off, cleanup.Token).ConfigureAwait(false); } catch (SqlException) { /* the session state is re-read afterwards */ }
                }
                return;
            }
            var options = SqlBulkCopyOptions.Default;
            if (o.KeepIdentity) options |= SqlBulkCopyOptions.KeepIdentity;
            if (o.FireTriggers) options |= SqlBulkCopyOptions.FireTriggers;
            if (o.CheckConstraints) options |= SqlBulkCopyOptions.CheckConstraints;
            if (o.TableLock) options |= SqlBulkCopyOptions.TableLock;
            var reader = new ConvertingReader(target, rows);
            // Batches mode: one transaction per chunk, so the committed count is exact when a later chunk fails
            // (SqlRowsCopied counts rows sent, including a batch that then fails to commit).
            var chunk = tx.Mode == BulkTxMode.Batches ? Math.Max(1, tx.CommitEvery) : int.MaxValue;
            long committed = 0;
            do
            {
                reader.StartChunk(chunk);
                await using var own = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                using var bulk = new SqlBulkCopy(conn, options, (SqlTransaction)own)
                {
                    DestinationTableName = $"[{target.Schema.Replace("]", "]]")}].[{target.Table.Replace("]", "]]")}]",
                    EnableStreaming = true,
                    BatchSize = Math.Min(chunk, 10_000),
                    BulkCopyTimeout = commandTimeoutS ?? 0,
                    NotifyAfter = 10_000,
                };
                foreach (var c in target.Columns) bulk.ColumnMappings.Add(c.Name, c.Name);
                var before = committed;
                bulk.SqlRowsCopied += (_, e) => rows.Loaded(before + e.RowsCopied, false);
                await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
                await tx.CommitAsync(() => own.CommitAsync(ct)).ConfigureAwait(false); // disposing without commit rolls back
                committed += reader.ChunkSent;
                rows.Loaded(committed, tx.Mode == BulkTxMode.Batches);
            } while (reader.More);
            rows.Loaded(committed, true);
        }
        finally { conn.FireInfoMessageEventOnUserErrors = asMessages; }
    }

    /// <summary>
    /// The rows as a forward-only data reader of typed values. A value the column type cannot hold rejects its row
    /// (reject log) and the reader moves on, so one bad row never fails the load.
    /// </summary>
    sealed class ConvertingReader(BulkTarget target, IBulkRows rows) : DbDataReader
    {
        readonly TypeSpec[] _types = target.Columns.Select(c => TypeSpec.Parse(c.Type)).ToArray();
        readonly object[] _values = new object[target.Columns.Count];
        int _limit = int.MaxValue;
        public long ChunkSent { get; private set; }
        /// <summary>The last chunk stopped at its limit: there may be more rows.</summary>
        public bool More { get; private set; }

        public void StartChunk(int limit)
        {
            _limit = limit;
            ChunkSent = 0;
            More = false;
        }

        public override bool Read()
        {
            if (ChunkSent >= _limit)
            {
                More = true;
                return false;
            }
            while (rows.Read())
            {
                var cur = rows.Current;
                string? bad = null;
                for (var i = 0; i < _values.Length; i++)
                {
                    var v = i < cur.Count ? cur[i] : null;
                    if (v == null) { _values[i] = DBNull.Value; continue; }
                    try { _values[i] = SqlServerDataEdit.Convert(_types[i], v); }
                    catch (FormatException fe)
                    {
                        bad = $"{target.Columns[i].Name}: {fe.Message}";
                        break;
                    }
                    if (_types[i] is { Max: false, A: { } len } t && t.Name is "char" or "varchar" or "nchar" or "nvarchar" && v.Length > len)
                    {
                        bad = $"{target.Columns[i].Name}: {v.Length} characters, the column holds {len}";
                        break;
                    }
                }
                if (bad != null) { rows.Reject(bad); continue; }
                ChunkSent++;
                return true;
            }
            return false;
        }

        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override int FieldCount => _values.Length;
        public override object GetValue(int ordinal) => _values[ordinal];
        public override bool IsDBNull(int ordinal) => _values[ordinal] is DBNull;
        public override string GetName(int ordinal) => target.Columns[ordinal].Name;
        public override int GetOrdinal(string name)
        {
            for (var i = 0; i < target.Columns.Count; i++) if (string.Equals(target.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new IndexOutOfRangeException(name);
        }
        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
        [UnconditionalSuppressMessage("Trimming", "IL2073", Justification = "Values are the primitive CLR types SqlBulkCopy maps; their members are never reflected on.")]
        public override Type GetFieldType(int ordinal) => _values[ordinal]?.GetType() ?? typeof(object);
        public override int GetValues(object[] values)
        {
            var n = Math.Min(values.Length, _values.Length);
            Array.Copy(_values, values, n);
            return n;
        }
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override bool HasRows => true;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override int Depth => 0;
        public override bool NextResult() => false;
        public override string GetDataTypeName(int ordinal) => target.Columns[ordinal].Type ?? "nvarchar";
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => (bool)_values[ordinal];
        public override byte GetByte(int ordinal) => (byte)_values[ordinal];
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        {
            var b = (byte[])_values[ordinal];
            if (buffer == null) return b.Length;
            var n = (int)Math.Min(length, b.Length - dataOffset);
            if (n <= 0) return 0;
            Array.Copy(b, dataOffset, buffer, bufferOffset, n);
            return n;
        }
        public override char GetChar(int ordinal) => ((string)_values[ordinal])[0];
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        {
            var s = (string)_values[ordinal];
            if (buffer == null) return s.Length;
            var n = (int)Math.Min(length, s.Length - dataOffset);
            if (n <= 0) return 0;
            s.CopyTo((int)dataOffset, buffer, bufferOffset, n);
            return n;
        }
        public override DateTime GetDateTime(int ordinal) => (DateTime)_values[ordinal];
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(_values[ordinal], CultureInfo.InvariantCulture);
        public override double GetDouble(int ordinal) => Convert.ToDouble(_values[ordinal], CultureInfo.InvariantCulture);
        public override float GetFloat(int ordinal) => Convert.ToSingle(_values[ordinal], CultureInfo.InvariantCulture);
        public override Guid GetGuid(int ordinal) => (Guid)_values[ordinal];
        public override short GetInt16(int ordinal) => Convert.ToInt16(_values[ordinal], CultureInfo.InvariantCulture);
        public override int GetInt32(int ordinal) => Convert.ToInt32(_values[ordinal], CultureInfo.InvariantCulture);
        public override long GetInt64(int ordinal) => Convert.ToInt64(_values[ordinal], CultureInfo.InvariantCulture);
        public override string GetString(int ordinal) => Convert.ToString(_values[ordinal], CultureInfo.InvariantCulture) ?? "";
    }
}
