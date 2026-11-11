using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Compare;

public enum DiffKind : byte { Different = 1, SourceOnly = 2, TargetOnly = 3 }

/// <summary>One difference: the row from each side (key columns first, then compared columns) and which compared columns differ.</summary>
public sealed record DiffRow(DiffKind Kind, string?[]? Source, string?[]? Target, byte[] Mask)
{
    public bool Differs(int column) => (Mask[column >> 3] & (1 << (column & 7))) != 0;
}

/// <summary>What one compare reads: key and compared columns with their kinds per side, in row order.</summary>
public sealed record ComparePlan(
    string[] KeyNames, ValueKind[] SourceKeyKinds, ValueKind[] TargetKeyKinds,
    string[] ValueNames, ValueKind[] SourceKinds, ValueKind[] TargetKinds,
    bool PaddedKeys, DataCompareOptions Options, bool EmptyIsNull = false)
{
    public int KeyCount => KeyNames.Length;
}

public sealed class CompareCounts
{
    public long SourceRows;
    public long TargetRows;
    public long Identical;
    public long Different;
    public long SourceOnly;
    public long TargetOnly;
}

/// <summary>
/// The streaming merge join: both sides arrive ordered by key; identical rows are only counted, each
/// difference goes to <c>onDiff</c>. A side whose keys repeat or arrive out of order stops the compare with a reason —
/// it would otherwise report false Source only / Target only pairs.
/// </summary>
public static class DataComparer
{
    sealed class Side(IAsyncEnumerator<string?[]> rows, ValueKind[] kinds, string role, ComparePlan plan)
    {
        IComparable[]? _last;
        public string?[]? Row;
        public IComparable[]? Key;

        public async ValueTask<bool> NextAsync()
        {
            if (!await rows.MoveNextAsync().ConfigureAwait(false)) { Row = null; Key = null; return false; }
            var row = rows.Current;
            var key = new IComparable[plan.KeyCount];
            for (var i = 0; i < key.Length; i++)
            {
                var text = row[i] ?? throw new QuerithmException("compareKey",
                    $"Key column {plan.KeyNames[i]} is NULL in a {role} row: rows with a NULL key cannot be matched. Pick other key columns.");
                key[i] = KeyComparers.Normalize(kinds[i], text, plan.PaddedKeys, plan.KeyNames[i]);
            }
            if (_last != null)
            {
                var c = KeyComparers.CompareKeys(_last, key);
                if (c == 0)
                    throw new QuerithmException("compareKey", $"The key ({string.Join(", ", plan.KeyNames)}) is not unique in the {role}: {Describe(row, plan.KeyCount)} appears twice.");
                if (c > 0)
                    throw new QuerithmException("compareOrder",
                        $"The {role} returned keys in an order the compare cannot reproduce (at {Describe(row, plan.KeyCount)}): its text order differs, e.g. trailing spaces or characters below space in keys.");
            }
            _last = key;
            Row = row;
            Key = key;
            return true;
        }
    }

    static string Describe(string?[] row, int keys) => "(" + string.Join(", ", row.Take(keys).Select(v => v ?? "NULL")) + ")";

    public static async Task MergeAsync(IAsyncEnumerator<string?[]> source, IAsyncEnumerator<string?[]> target, ComparePlan plan, CompareCounts counts,
        Func<DiffRow, ValueTask> onDiff, CancellationToken ct)
    {
        var s = new Side(source, plan.SourceKeyKinds, "source", plan);
        var t = new Side(target, plan.TargetKeyKinds, "target", plan);
        var hasS = await s.NextAsync().ConfigureAwait(false);
        var hasT = await t.NextAsync().ConfigureAwait(false);
        var maskBytes = (plan.ValueNames.Length + 7) / 8;
        while (hasS || hasT)
        {
            ct.ThrowIfCancellationRequested();
            var c = !hasS ? 1 : !hasT ? -1 : KeyComparers.CompareKeys(s.Key!, t.Key!);
            if (c < 0)
            {
                counts.SourceRows++;
                counts.SourceOnly++;
                await onDiff(new DiffRow(DiffKind.SourceOnly, s.Row, null, new byte[maskBytes])).ConfigureAwait(false);
                hasS = await s.NextAsync().ConfigureAwait(false);
                continue;
            }
            if (c > 0)
            {
                counts.TargetRows++;
                counts.TargetOnly++;
                await onDiff(new DiffRow(DiffKind.TargetOnly, null, t.Row, new byte[maskBytes])).ConfigureAwait(false);
                hasT = await t.NextAsync().ConfigureAwait(false);
                continue;
            }
            counts.SourceRows++;
            counts.TargetRows++;
            byte[]? mask = null;
            for (var i = 0; i < plan.ValueNames.Length; i++)
            {
                var k = plan.KeyCount + i;
                var a = s.Row![k];
                var b = t.Row![k];
                // Oracle keeps '' as NULL: with it on either side the two are one value.
                if (plan.EmptyIsNull) { if (a == "") a = null; if (b == "") b = null; }
                if (KeyComparers.ValuesEqual(plan.SourceKinds[i], plan.TargetKinds[i], a, b, plan.Options)) continue;
                mask ??= new byte[maskBytes];
                mask[i >> 3] |= (byte)(1 << (i & 7));
            }
            if (mask == null) counts.Identical++;
            else
            {
                counts.Different++;
                await onDiff(new DiffRow(DiffKind.Different, s.Row, t.Row, mask)).ConfigureAwait(false);
            }
            hasS = await s.NextAsync().ConfigureAwait(false);
            hasT = await t.NextAsync().ConfigureAwait(false);
        }
    }
}
