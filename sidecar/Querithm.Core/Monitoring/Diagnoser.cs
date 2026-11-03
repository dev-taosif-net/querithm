using System.Globalization;
using Querithm.Contract;

namespace Querithm.Core.Monitoring;

/// <summary>
/// "Why is it slow?" (B3b): a fixed, deterministic rule set over data the monitor already has — snapshot, blocking
/// graph, request detail and kill preview. No LLM, offline. Each finding carries the evidence values it used;
/// identifiers are data, never markup (the UI renders them as text).
/// </summary>
public static class Diagnoser
{
    static readonly string[] SeverityOrder = ["high", "medium", "low", "info"];
    public const long ScanReads = 100_000;
    public const long LongWaitMs = 1_000;
    public const long LongTxMs = 10 * 60_000;

    static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    public static Diagnosis Run(SessionKey key, ActivityRow? row, IReadOnlyList<ActivityRow> rows, KillPreview? preview, RequestDetail? detail,
        WaitDelta[]? waits, Engine engine, WaitDictionary dictionary)
    {
        if (row == null)
            return new Diagnosis
            {
                Key = key,
                Findings = [new DiagnosisFinding { Rule = "sessionEnded", Severity = "info", Message = "The session ended.", Evidence = [] }],
                Evidence = [],
            };
        var findings = new List<(int Order, DiagnosisFinding F)>();
        var notes = new List<string>();
        var byId = rows.GroupBy(r => r.Key.Id).ToDictionary(g => g.Key, g => g.First());
        void Add(int order, DiagnosisFinding f) => findings.Add((order, f));
        var waitEvidence = row.WaitType != null ? $"wait {row.WaitType}" + (row.WaitMs is { } wm ? $" for {N(wm)} ms" : "") : null;

        // 1. Blocked: by whom, and what the blocker is doing.
        foreach (var b in row.BlockedBy)
        {
            if (!byId.TryGetValue(b, out var blocker))
            {
                Add(1, new DiagnosisFinding
                {
                    Rule = "hiddenBlocker", Severity = "high", BlockerId = b,
                    Message = $"Blocked by session {b}, which this monitor cannot see (permission, filter or the session cap).",
                    NextStep = "Grant VIEW SERVER STATE / pg_read_all_stats to see the blocker, or clear the database filter.",
                    Evidence = [$"blocked by {b}", .. waitEvidence is null ? Array.Empty<string>() : [waitEvidence]],
                });
                continue;
            }
            var idle = blocker.Status is "sleeping" or "idle in transaction" or "idle in transaction (aborted)";
            var ev = new List<string> { $"blocked by {b}", $"blocker status {blocker.Status}" };
            if (blocker.Program != null) ev.Add($"blocker application {blocker.Program}");
            if (blocker.TxElapsedMs is { } tx) ev.Add($"blocker transaction open {N(tx / 1000)} s");
            if (waitEvidence != null) ev.Add(waitEvidence);
            if (blocker.OwnSessionId != null)
                Add(0, new DiagnosisFinding
                {
                    Rule = "blocked", Severity = "high", BlockerId = b, OwnSessionId = blocker.OwnSessionId,
                    Message = $"Blocked by session {b}: one of your Querithm tabs" + (idle ? " with an open transaction that is not running anything." : "."),
                    NextStep = "Go to the tab and commit or roll back.",
                    Evidence = [.. ev],
                });
            else if (idle && (blocker.OpenTx ?? 0) > 0)
                Add(0, new DiagnosisFinding
                {
                    Rule = "idleBlocker", Severity = "high", BlockerId = b,
                    Message = $"Blocked by session {b}, which is idle with an open transaction.",
                    NextStep = "Ask its owner to commit, or terminate the blocker (the kill dialog shows the rollback cost).",
                    Evidence = [.. ev],
                });
            else
                Add(1, new DiagnosisFinding
                {
                    Rule = "blocked", Severity = "high", BlockerId = b,
                    Message = $"Blocked by session {b} ({blocker.Status}).",
                    NextStep = "Look at what the blocker runs (open its detail); it may itself be waiting.",
                    Evidence = [.. ev],
                });
        }

        var category = row.WaitType is { } wt ? CategoryOf(engine, wt, dictionary) : null;
        // 2. Lock wait with no visible blocker.
        if (row.BlockedBy.Length == 0 && category == "Lock" && row.BlockedByNote == null)
            Add(2, new DiagnosisFinding
            {
                Rule = "hiddenBlocker", Severity = "high",
                Message = "Waiting on a lock, but no blocking session is visible (permission).",
                NextStep = "Grant VIEW SERVER STATE / pg_read_all_stats to see who holds the lock.",
                Evidence = waitEvidence is null ? [] : [waitEvidence],
            });
        if (row.BlockedByNote is { } note)
            Add(2, new DiagnosisFinding
            {
                Rule = "blocked", Severity = "high", Message = $"Blocked by {BlockingGraph.NoteReason(note)}.",
                NextStep = "This is not a session that can be killed; see the server's transaction or latch diagnostics.",
                Evidence = [$"blocking_session_id {BlockingGraph.NoteId(note)}"],
            });

        // 3. Memory grant (SQL Server).
        if (row.WaitType is "RESOURCE_SEMAPHORE" or "RESOURCE_SEMAPHORE_QUERY_COMPILE")
            Add(3, new DiagnosisFinding
            {
                Rule = "memoryGrant", Severity = "high",
                Message = "Waiting for a memory grant.",
                NextStep = "Check which queries hold large grants; large sorts or hashes may need better estimates or indexes.",
                Evidence = [.. new[] { waitEvidence, row.RequestedMemoryKb is { } rq ? $"requested {N(rq)} KB" : null, row.GrantedMemoryKb is { } g ? $"granted {N(g)} KB" : "granted none yet" }.OfType<string>()],
            });

        // 4. Parallel skew.
        if (row.WaitType is "CXPACKET" or "CXCONSUMER" or "CXSYNC_PORT" or "CXSYNC_CONSUMER" || (MonitorRules.For(engine).WaitTypeHasClass && category == "Parallelism"))
            Add(4, new DiagnosisFinding
            {
                Rule = "parallelSkew", Severity = "medium",
                Message = "Parallel workers are waiting on each other (uneven work across threads).",
                NextStep = "Open the plan: look for skewed row counts per thread; consider MAXDOP or better statistics.",
                Evidence = [.. new[] { waitEvidence, row.Dop is { } dop ? $"DOP {dop}" : null }.OfType<string>()],
            });

        // 5. Scan: many logical reads per row.
        if (row.LogicalReads is { } lr && lr >= ScanReads && lr > 1_000 * Math.Max(1, row.RowCount ?? 0))
            Add(5, new DiagnosisFinding
            {
                Rule = "scan", Severity = "medium",
                Message = "Many logical reads for the rows returned: likely a scan.",
                NextStep = "Open the plan and look for scans and missing indexes.",
                Evidence = [$"{N(lr)} logical reads", $"{N(row.RowCount ?? 0)} rows"],
            });

        // 6. Plan warnings (B5).
        if (detail?.Plan is { } plan)
        {
            var warnings = plan.Plan.Statements.SelectMany(s => s.Warnings.Concat(Nodes(s.Root).SelectMany(n => n.Warnings.Select(w => w.Message)))).Distinct().Take(5).ToArray();
            var missing = plan.Plan.Statements.SelectMany(s => s.MissingIndexes).Take(3).ToArray();
            if (warnings.Length > 0 || missing.Length > 0)
                Add(6, new DiagnosisFinding
                {
                    Rule = "planWarning", Severity = "medium",
                    Message = "The plan has warnings" + (missing.Length > 0 ? " and a missing-index suggestion." : "."),
                    NextStep = "Open the plan to see where they apply.",
                    Evidence = [.. warnings, .. missing.Select(m => $"missing index on {m.Table} (impact {m.Impact:0.#})")],
                });
        }
        else if (detail != null) notes.Add(detail.PlanReason ?? "No plan available; plan rules skipped.");
        else notes.Add("Request detail could not be read; plan rules skipped.");

        // 7. PostgreSQL: idle in transaction holding locks others wait on, long transactions, wait classes.
        if (row.Status.StartsWith("idle in transaction", StringComparison.Ordinal))
        {
            var victims = BlockingGraph.VictimCount(rows, row.Key.Id);
            Add(victims > 0 ? 0 : 7, new DiagnosisFinding
            {
                Rule = "idleInTx", Severity = victims > 0 ? "high" : "medium",
                Message = victims > 0 ? $"Idle in transaction while {victims} session(s) wait on its locks." : "Idle in transaction: it holds its snapshot and locks until it ends.",
                NextStep = "Commit or roll back in the application; terminate it only if its owner is gone.",
                Evidence = [.. new[] { $"status {row.Status}", row.TxElapsedMs is { } t ? $"transaction open {N(t / 1000)} s" : null, victims > 0 ? $"{victims} waiting" : null }.OfType<string>()],
            });
        }
        else if (row.TxElapsedMs is { } txMs && txMs >= LongTxMs)
            Add(8, new DiagnosisFinding
            {
                Rule = "longTx", Severity = "medium",
                Message = $"The transaction has been open for {N(txMs / 60_000)} min.",
                NextStep = "Long transactions hold locks and (PostgreSQL) keep vacuum from cleaning up; check whether it should still be open.",
                Evidence = [$"transaction open {N(txMs / 1000)} s"],
            });
        if (MonitorRules.For(engine).WaitTypeHasClass && row.WaitType is { } pgw && pgw.Split(':')[0] is "IO" or "LWLock" or "Lock" && row.BlockedBy.Length == 0)
        {
            var info = dictionary.Info(engine, pgw);
            Add(9, new DiagnosisFinding
            {
                Rule = "waitClass", Severity = "medium",
                Message = $"Waiting on {pgw}: {(info.Known ? info.Meaning : "no description yet")}",
                NextStep = info.Check is { Length: > 0 } c ? c[0] : null,
                Evidence = waitEvidence is null ? [] : [waitEvidence],
            });
        }

        // 8. One long wait: explained from the dictionary.
        if (row.WaitType is { } w && (row.WaitMs ?? 0) >= LongWaitMs && category is not "Lock" && !(MonitorRules.For(engine).WaitTypeHasClass && w.Split(':')[0] is "IO" or "LWLock" or "Lock")
            && w is not "RESOURCE_SEMAPHORE" and not "RESOURCE_SEMAPHORE_QUERY_COMPILE" and not "CXPACKET" and not "CXCONSUMER")
        {
            var info = dictionary.Info(engine, w);
            Add(10, new DiagnosisFinding
            {
                Rule = "waitType", Severity = "low",
                Message = $"Waiting on {w}: {(info.Known ? info.Meaning : "no description yet")}",
                NextStep = info.Check is { Length: > 0 } c ? c[0] : null,
                Evidence = waitEvidence is null ? [] : [waitEvidence],
            });
        }

        if (preview is { Risk: RollbackRisk.High } p)
            notes.Add($"Terminating it would roll back {(p.LogBytesUsed is { } lb ? $"{N(lb / 1_048_576)} MB of log" : "a long transaction")}; the rollback may take long.");

        // Nothing matched.
        if (findings.Count == 0)
            Add(99, new DiagnosisFinding
            {
                Rule = "onCpu", Severity = "info",
                Message = "No obvious cause: it is running on CPU (no blocking, no notable wait).",
                NextStep = "Open the plan to see where the work goes.",
                Evidence = [.. new[] { row.CpuMs is { } cpu ? $"CPU {N(cpu)} ms" : "CPU not reported", row.LogicalReads is { } l ? $"{N(l)} logical reads" : null, row.Reads is { } r ? $"{N(r)} reads" : null,
                    row.ElapsedMs is { } e ? $"elapsed {N(e)} ms" : null }.OfType<string>()],
            });

        var ordered = findings
            .OrderBy(f => Array.IndexOf(SeverityOrder, f.F.Severity))
            .ThenBy(f => f.Order)
            .ThenBy(f => f.F.BlockerId ?? 0)
            .Select(f => f.F)
            .ToArray();
        return new Diagnosis
        {
            Key = key,
            Findings = ordered,
            Evidence = ordered.SelectMany(f => f.Evidence).Distinct().ToArray(),
            Notes = notes.Count > 0 ? [.. notes] : null,
        };
    }

    static string? CategoryOf(Engine engine, string waitType, WaitDictionary dictionary) =>
        dictionary.Info(engine, waitType) is { Known: true } i ? i.Category
        : waitType.StartsWith(MonitorRules.For(engine).LockWaitPrefix, StringComparison.Ordinal) ? "Lock" : null;

    static IEnumerable<PlanNode> Nodes(PlanNode? root)
    {
        if (root == null) yield break;
        var stack = new Stack<PlanNode>([root]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.Children) stack.Push(c);
        }
    }
}
