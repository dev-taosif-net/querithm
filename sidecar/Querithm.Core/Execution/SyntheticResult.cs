using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Querithm.Core.Execution;

/// <summary>Hard-coded wide result for milestone 1 and bench metrics 11–13: N rows × M mixed-type columns.</summary>
public static class SyntheticResult
{
    static readonly string[] Words = ["Acme Global Corp", "Northwind", "Contoso Ltd", "Fabrikam", "Tailspin Toys", "Wide World Importers", "Adventure Works", "Litware"];

    public static Schema BuildSchema(int columns)
    {
        var fields = new List<Field>();
        for (var c = 0; c < columns; c++)
        {
            var (name, type, typeName) = (c % 8) switch
            {
                0 => ($"Id{c}", (IArrowType)Int64Type.Default, "bigint"),
                1 => ($"Qty{c}", Int32Type.Default, "int"),
                2 => ($"Ratio{c}", DoubleType.Default, "float"),
                3 => ($"Amount{c}", new Decimal128Type(18, 2), "decimal"),
                4 => ($"Name{c}", StringType.Default, "nvarchar"),
                5 => ($"CreatedAt{c}", new TimestampType(TimeUnit.Microsecond, (string?)null), "datetime2"),
                6 => ($"Active{c}", BooleanType.Default, "bit"),
                _ => ($"Day{c}", Date32Type.Default, "date"),
            };
            fields.Add(new Field(c == 0 ? "OrderId" : name, type, true, new Dictionary<string, string> { ["q:typeName"] = typeName }));
        }
        return new Schema(fields, null);
    }

    public static async IAsyncEnumerable<(byte Kind, byte[] Payload)> Generate(int rows, int columns, [EnumeratorCancellation] CancellationToken ct)
    {
        var schema = BuildSchema(columns);
        yield return (Core.FrameKind.Schema, ResultSetEncoder.ToIpc(schema, null));
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var start = 0; start < rows; start += ResultSetEncoder.MaxBatchRows)
        {
            ct.ThrowIfCancellationRequested();
            var n = Math.Min(ResultSetEncoder.MaxBatchRows, rows - start);
            var arrays = new IArrowArray[columns];
            for (var c = 0; c < columns; c++)
            {
                switch (c % 8)
                {
                    case 0:
                    {
                        var b = new Int64Array.Builder().Reserve(n);
                        for (var i = 0; i < n; i++) b.Append(1_049_201L + start + i);
                        arrays[c] = b.Build(); break;
                    }
                    case 1:
                    {
                        var b = new Int32Array.Builder().Reserve(n);
                        for (var i = 0; i < n; i++) b.Append((start + i) % 977);
                        arrays[c] = b.Build(); break;
                    }
                    case 2:
                    {
                        var b = new DoubleArray.Builder().Reserve(n);
                        for (var i = 0; i < n; i++) b.Append(((start + i) * 7919 % 10_000) / 10_000.0);
                        arrays[c] = b.Build(); break;
                    }
                    case 3:
                    {
                        var b = new Decimal128Array.Builder(new Decimal128Type(18, 2));
                        b.Reserve(n);
                        for (var i = 0; i < n; i++) b.Append(((start + i) * 31 % 100_000) + 0.75m);
                        arrays[c] = b.Build(); break;
                    }
                    case 4:
                    {
                        var b = new StringArray.Builder();
                        for (var i = 0; i < n; i++) b.Append(Words[(start + i + c) % Words.Length]);
                        arrays[c] = b.Build(); break;
                    }
                    case 5:
                    {
                        var b = new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, (string?)null));
                        b.Reserve(n);
                        for (var i = 0; i < n; i++) b.Append(epoch.AddSeconds(start + i));
                        arrays[c] = b.Build(); break;
                    }
                    case 6:
                    {
                        var b = new BooleanArray.Builder().Reserve(n);
                        for (var i = 0; i < n; i++) b.Append(((start + i) & 1) == 0);
                        arrays[c] = b.Build(); break;
                    }
                    default:
                    {
                        var b = new Date32Array.Builder().Reserve(n);
                        for (var i = 0; i < n; i++) b.Append(epoch.AddDays((start + i) % 365).DateTime);
                        arrays[c] = b.Build(); break;
                    }
                }
            }
            using var batch = new RecordBatch(schema, arrays, n);
            yield return (Core.FrameKind.Batch, ResultSetEncoder.ToIpc(schema, batch));
            await Task.Yield();
        }
        yield return (Core.FrameKind.EndOfResultSet, [0]);
    }
}
