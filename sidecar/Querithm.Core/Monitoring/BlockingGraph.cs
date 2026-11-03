using Querithm.Contract;

namespace Querithm.Core.Monitoring;

/// <summary>
/// The head-blocker forest of a snapshot (B2): edges from BlockedBy (self-edges already dropped by the providers),
/// cycles marked as "deadlock in progress" instead of recursed into, and placeholders for blockers that are not rows —
/// SQL Server pseudo-blockers (-2/-3/-4, never sessions) and sessions hidden by truncation, filter or permission.
/// </summary>
public static class BlockingGraph
{
    public static BlockingInfo Build(IReadOnlyList<ActivityRow> rows, string? hiddenReason = null)
    {
        var byId = new Dictionary<long, ActivityRow>(rows.Count);
        foreach (var r in rows) byId.TryAdd(r.Key.Id, r);
        var placeholders = new Dictionary<long, BlockingPlaceholder>();
        var blockers = new HashSet<long>();
        var blocked = 0;
        foreach (var r in rows)
        {
            if (r.BlockedBy.Length > 0 || r.BlockedByNote != null) blocked++;
            foreach (var b in r.BlockedBy)
            {
                if (b == r.Key.Id) continue;
                blockers.Add(b);
                if (!byId.ContainsKey(b))
                    placeholders.TryAdd(b, new BlockingPlaceholder { Id = b, IsSession = true, Reason = $"session hidden ({hiddenReason ?? "not in this snapshot: truncated, filtered or not visible"})" });
            }
            if (r.BlockedByNote is { } note)
            {
                var id = NoteId(note);
                placeholders.TryAdd(id, new BlockingPlaceholder { Id = id, IsSession = false, Reason = NoteReason(note) });
            }
        }

        var cycles = Cycles(rows, byId);
        var inCycle = new HashSet<long>(cycles.SelectMany(c => c));
        var heads = new List<long>();
        foreach (var b in blockers.Order())
        {
            if (inCycle.Contains(b)) continue;
            if (!byId.TryGetValue(b, out var row) || (row.BlockedBy.Length == 0 && row.BlockedByNote == null)) heads.Add(b);
        }
        foreach (var p in placeholders.Values.Where(p => !p.IsSession).OrderBy(p => p.Id)) heads.Add(p.Id);
        return new BlockingInfo
        {
            HeadBlockers = [.. heads],
            Cycles = cycles.Select(c => c.Order().ToArray()).ToArray(),
            Placeholders = [.. placeholders.Values.OrderBy(p => p.Id)],
            BlockedCount = blocked,
        };
    }

    public static long NoteId(string note) => note switch
    {
        "orphanedDtc" => -2,
        "deferredRecovery" => -3,
        "latchOwnerUnknown" => -4,
        _ => -1,
    };

    public static string NoteReason(string note) => note switch
    {
        "orphanedDtc" => "orphaned distributed transaction (-2): not a session",
        "deferredRecovery" => "deferred recovery transaction (-3): not a session",
        "latchOwnerUnknown" => "latch owner not determinable (-4): not a session",
        _ => "not a session",
    };

    /// <summary>Strongly connected components of size > 1 (or a self-loop) over blocked → blocker edges (Tarjan, iterative).</summary>
    static List<List<long>> Cycles(IReadOnlyList<ActivityRow> rows, Dictionary<long, ActivityRow> byId)
    {
        var index = new Dictionary<long, int>();
        var low = new Dictionary<long, int>();
        var onStack = new HashSet<long>();
        var stack = new Stack<long>();
        var result = new List<List<long>>();
        var next = 0;
        foreach (var root in rows.Where(r => r.BlockedBy.Length > 0).Select(r => r.Key.Id))
        {
            if (index.ContainsKey(root)) continue;
            var work = new Stack<(long Node, int Edge)>();
            work.Push((root, 0));
            index[root] = low[root] = next++;
            stack.Push(root);
            onStack.Add(root);
            while (work.Count > 0)
            {
                var (v, i) = work.Pop();
                var edges = byId.TryGetValue(v, out var row) ? row.BlockedBy : [];
                if (i < edges.Length)
                {
                    work.Push((v, i + 1));
                    var w = edges[i];
                    if (!byId.ContainsKey(w)) continue;
                    if (!index.ContainsKey(w))
                    {
                        index[w] = low[w] = next++;
                        stack.Push(w);
                        onStack.Add(w);
                        work.Push((w, 0));
                    }
                    else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
                    continue;
                }
                if (work.Count > 0) { var parent = work.Peek().Node; low[parent] = Math.Min(low[parent], low[v]); }
                if (low[v] != index[v]) continue;
                var comp = new List<long>();
                long x;
                do { x = stack.Pop(); onStack.Remove(x); comp.Add(x); } while (x != v);
                if (comp.Count > 1) result.Add(comp);
            }
        }
        return result;
    }

    /// <summary>Sessions waiting (directly or through others) on <paramref name="id"/>, each counted once (B4 multi-blocker).</summary>
    public static int VictimCount(IReadOnlyList<ActivityRow> rows, long id)
    {
        var waitingOn = new Dictionary<long, List<long>>();
        foreach (var r in rows)
            foreach (var b in r.BlockedBy)
            {
                if (!waitingOn.TryGetValue(b, out var l)) waitingOn[b] = l = [];
                l.Add(r.Key.Id);
            }
        var seen = new HashSet<long>();
        var queue = new Queue<long>([id]);
        while (queue.Count > 0)
            foreach (var w in waitingOn.GetValueOrDefault(queue.Dequeue()) ?? [])
                if (w != id && seen.Add(w)) queue.Enqueue(w);
        return seen.Count;
    }
}
