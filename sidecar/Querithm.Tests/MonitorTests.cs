using System.Text;
using System.Text.Json;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Monitoring;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// Activity monitor building blocks with fakes (Phase B matrix rows typed U). Serial: B10 times a 5 ms bound, the
/// evidence for its matrix row, which parallel classes made fail.
/// </summary>
[Collection("timing")]
public class MonitorTests
{
    static SessionKey K(long id, string inst = "1:1") => new() { Id = id, Instance = inst };

    static ActivityRow Row(long id, string status = "running", long[]? blockedBy = null, string? textId = null, bool user = true, string? wait = null,
        long? waitMs = null, string? note = null, string inst = "1:1") => new()
    {
        Key = K(id, inst), Status = status, BlockedBy = blockedBy ?? [], TextId = textId, IsUserSession = user, WaitType = wait, WaitMs = waitMs, BlockedByNote = note,
    };

    // ------------------------------------------------------------------ text store (payload)

    [Fact]
    public void S5_a_text_over_4KB_is_cut_on_a_character_boundary_without_splitting_a_surrogate_pair()
    {
        var text = new string('a', 4_094) + "😀😀😀";
        var p = TextStore.Preview(text);
        Assert.True(Encoding.UTF8.GetByteCount(p) <= TextStore.PreviewBytes);
        Assert.False(char.IsHighSurrogate(p[^1]));
        Assert.Equal(new string('a', 4_094), p);
        var multi = string.Concat(Enumerable.Repeat("é", 3_000)); // 2 bytes each
        Assert.Equal(2_048, TextStore.Preview(multi).Length);
    }

    [Fact]
    public void M5_S17_texts_are_sent_once_and_an_unchanged_session_text_is_not_rehashed()
    {
        var store = new TextStore();
        var a = store.InternForSession(K(1), "SELECT 1");
        var sent = store.Commit([a], [K(1)]);
        Assert.Equal("SELECT 1", sent![a]);
        var hashes = store.HashCount;
        var again = store.InternForSession(K(1), "SELECT 1");
        Assert.Equal(a, again);
        Assert.Equal(hashes, store.HashCount);
        Assert.Null(store.Commit([again], [K(1)]));
    }

    [Fact]
    public void S15_S16_the_text_id_is_the_content_hash_so_changed_text_gets_a_new_id_even_on_the_same_session()
    {
        var store = new TextStore();
        // Same query_id / sql_handle on the server does not matter: only the text decides.
        var one = store.InternForSession(K(1), "UPDATE t SET v = 1 WHERE id = 1");
        var two = store.InternForSession(K(2), "UPDATE t SET v = 1 WHERE id = 2");
        Assert.NotEqual(one, two);
        var altered = store.InternForSession(K(1), "UPDATE t SET v = 2 WHERE id = 1");
        Assert.NotEqual(one, altered);
        Assert.True(TextStore.IsValidId(one));
    }

    [Fact]
    public void M8_sustained_text_churn_keeps_the_store_at_live_plus_4MB()
    {
        var store = new TextStore();
        long maxUnref = 0;
        for (var tick = 0; tick < 1_800; tick++) // 1 h at 2 s
        {
            var ids = new List<string>();
            var keys = new List<SessionKey>();
            for (var s = 0; s < 500; s++)
            {
                keys.Add(K(s));
                ids.Add(store.InternForSession(K(s), $"SELECT * FROM t WHERE id = {tick * 1000 + s} /* {new string('x', 400)} */"));
            }
            store.Commit(ids, keys);
            maxUnref = Math.Max(maxUnref, store.UnreferencedBytes);
            Assert.True(store.TotalBytes <= store.LiveBytes + TextStore.UnreferencedCapBytes + 4_096, $"tick {tick}: {store.TotalBytes}");
        }
        Assert.True(maxUnref <= TextStore.UnreferencedCapBytes);
    }

    [Fact]
    public void M9_M10_M11_live_texts_are_never_evicted_evicted_ones_are_missing_and_sent_again_when_they_reappear()
    {
        var store = new TextStore { UnreferencedCap = 10_000 };
        var live = store.Intern("live text");
        var ended = store.Intern("ended session text");
        store.Commit([live, ended]);
        store.Commit([live]); // the session with `ended` is gone: unreferenced
        for (var i = 0; i < 100; i++) { var x = store.Intern($"filler {i} {new string('z', 200)}"); store.Commit([live, x]); }
        var (found, missing) = store.Get([live, ended]);
        Assert.Contains(live, found.Keys); // M10
        Assert.Contains(ended, missing);   // M9
        var back = store.Intern("ended session text");
        Assert.Equal(ended, back);
        var sent = store.Commit([live, back]);
        Assert.True(sent!.ContainsKey(back)); // M11: new to the store, so sent again
    }

    [Fact]
    public void H8_texts_named_only_by_history_are_kept_counted_and_freed_with_their_ticks()
    {
        var store = new TextStore { UnreferencedCap = 0 };
        var id = store.Intern("old query");
        store.Commit([id]);
        store.AddHistoryRefs([id]);
        store.Commit([]);
        Assert.NotNull(store.TryGet(id));
        Assert.True(store.HistoryBytes > 0);
        store.RemoveHistoryRefs([id]);
        Assert.Null(store.TryGet(id));
    }

    // ------------------------------------------------------------------ deltas

    [Fact]
    public void D1_to_D5_first_sample_collects_resets_and_wraps_suppress_and_new_keys_count_from_zero()
    {
        var t = new DeltaTracker<string>();
        var r = t.Update([("a", 100), ("b", 10)], 0);
        Assert.True(r.First); Assert.Empty(r.Deltas);                        // D1
        r = t.Update([("a", 130), ("b", 10), ("c", 5)], 4_000);
        Assert.True(r.HasDeltas);
        Assert.Equal(30, r.Deltas["a"]); Assert.Equal(5, r.Deltas["c"]);  // D5
        Assert.Equal(4_000, r.ElapsedMs);                                  // D4: real elapsed, not nominal
        r = t.Update([("a", 3), ("b", 10), ("c", 5)], 6_000);
        Assert.True(r.Reset); Assert.Empty(r.Deltas);                      // D2: counter reset
        r = t.Update([("a", 8), ("b", 10), ("c", 5)], 8_000, "start-1");
        Assert.True(r.HasDeltas);
        r = t.Update([("a", 9), ("b", 11), ("c", 6)], 10_000, "start-2");
        Assert.True(r.Reset);                                               // restart marker changed
        r = t.Update([("a", long.MaxValue - 1)], 12_000, "start-2");
        r = t.Update([("a", 0)], 14_000, "start-2");
        Assert.True(r.Reset);                                               // D3: wrap is a reset, never negative
    }

    [Fact]
    public void D2_a_read_that_lost_nearly_every_counter_was_cleared_and_is_a_reset()
    {
        // DBCC SQLPERF('sys.dm_os_wait_stats', CLEAR) zeroes the counters, and the read skips zero ones: right after it,
        // 26 non-zero wait types became 1 (9 a second later, nearly all benign) — the keys vanish instead of going down.
        var t = new DeltaTracker<string>();
        var before = Enumerable.Range(0, 20).Select(i => ($"w{i}", 1_000.0 + i)).ToArray();
        t.Update(before, 0);
        t.Update(before, 1_000);
        var r = t.Update([("new-after-clear", 3)], 2_000);
        Assert.True(r.Reset);
        Assert.Empty(r.Deltas);
        // A few keys going away (a dropped database's files) is not a reset: they are just dropped.
        t.Update(before, 3_000);
        r = t.Update(before.Skip(3), 4_000);
        Assert.False(r.Reset);
        Assert.True(r.HasDeltas);
    }

    [Fact]
    public void D9_D10_D11_tick_sampled_waits_weight_by_real_elapsed_time_capped_at_two_intervals()
    {
        var s = new TickWaitSampler();
        var rows = new[] { Row(1, "active", wait: "Lock:relation"), Row(2, "active", wait: "Lock:relation"), Row(3, "idle", wait: "Client:ClientRead"), Row(4, "active", wait: "Activity:WalWriterMain") };
        Assert.Null(s.Sample(rows, 0, 2_000, _ => "Lock", w => w.StartsWith("Activity", StringComparison.Ordinal))); // first tick: collecting
        var d = s.Sample(rows, 2_000, 2_000, _ => "Lock", w => w.StartsWith("Activity", StringComparison.Ordinal))!;
        var lockWait = Assert.Single(d);
        Assert.Equal(4_000, lockWait.WaitMsDelta); // two backends × 2 s
        Assert.Equal(2, lockWait.TasksDelta);
        Assert.Null(lockWait.SignalMsDelta);
        var late = s.Sample(rows, 2_000 + 3 * 2_000, 2_000, _ => "Lock", _ => false)!; // 3 intervals late
        Assert.Equal(2 * 2 * 2_000, late.Single(w => w.WaitType == "Lock:relation").WaitMsDelta); // capped at 2 × interval each
    }

    [Fact]
    public void Metrics_rates_use_elapsed_time_and_a_missing_value_is_never_zero()
    {
        var b = new MetricsBuilder();
        MetricCounter[] C(double batch, double? cpu) => [new("batchRequests", batch, true, "perSec"), new("cpu", cpu, false, "percent", cpu == null ? "n/a (ring buffer format)" : "SQL Server process"), new("ple", 300, false, "seconds")];
        var (first, st1) = b.Build(C(100, 5), 0, 0, null);
        Assert.Equal(PaneState.Collecting, st1);
        Assert.Null(first.Values.Single(v => v.Key == "batchRequests").Value);
        Assert.Equal(300, first.Values.Single(v => v.Key == "ple").Value);
        var (second, st2) = b.Build(C(160, null), 3_000, 3_000, null);
        Assert.Equal(PaneState.Ok, st2);
        Assert.Equal(20, second.Values.Single(v => v.Key == "batchRequests").Value);
        var cpu = second.Values.Single(v => v.Key == "cpu");
        Assert.Null(cpu.Value);                                   // D17: never presented as 0
        Assert.Equal("n/a (ring buffer format)", cpu.Note);
    }

    // ------------------------------------------------------------------ blocking graph

    [Fact]
    public void B1_B2_B3_chains_and_fan_out()
    {
        var rows = new[] { Row(1, "sleeping"), Row(2, blockedBy: [1]), Row(3, blockedBy: [2]), Row(4, blockedBy: [1]), Row(5, blockedBy: [1]) };
        var g = BlockingGraph.Build(rows);
        Assert.Equal([1L], g.HeadBlockers);
        Assert.Equal(4, g.BlockedCount);
        Assert.Equal(4, BlockingGraph.VictimCount(rows, 1));
        Assert.Equal(1, BlockingGraph.VictimCount(rows, 2));
    }

    [Fact]
    public void B4_multi_blocker_is_counted_once()
    {
        var rows = new[] { Row(1), Row(3), Row(2, blockedBy: [1, 3]) };
        var g = BlockingGraph.Build(rows);
        Assert.Equal([1L, 3L], g.HeadBlockers);
        Assert.Equal(1, g.BlockedCount);
    }

    [Fact]
    public void B6_a_cycle_is_marked_as_deadlock_in_progress_without_recursing()
    {
        var rows = new[] { Row(1, blockedBy: [2]), Row(2, blockedBy: [1]), Row(3, blockedBy: [2]) };
        var g = BlockingGraph.Build(rows);
        var cycle = Assert.Single(g.Cycles);
        Assert.Equal([1L, 2L], cycle);
        Assert.Empty(g.HeadBlockers);
        Assert.Equal(3, BlockingGraph.VictimCount(rows, 1) + 1);
    }

    [Fact]
    public void B7_B11_hidden_blockers_and_negative_pseudo_blockers_become_placeholders_never_sessions()
    {
        var rows = new[] { Row(2, blockedBy: [99]), Row(3, note: "orphanedDtc"), Row(4, note: "latchOwnerUnknown") };
        var g = BlockingGraph.Build(rows);
        var hidden = g.Placeholders.Single(p => p.Id == 99);
        Assert.True(hidden.IsSession);
        Assert.Contains("session hidden", hidden.Reason);
        var dtc = g.Placeholders.Single(p => p.Id == -2);
        Assert.False(dtc.IsSession);
        Assert.Contains("not a session", dtc.Reason);
        Assert.Contains(-2L, g.HeadBlockers);
        Assert.Contains(-4L, g.HeadBlockers);
        Assert.Contains(99L, g.HeadBlockers);
    }

    [Fact]
    public void B10_a_1000_node_tree_builds_within_5ms()
    {
        var rows = Enumerable.Range(1, 1_000).Select(i => Row(i, blockedBy: i == 1 ? [] : [Math.Max(1, i / 2)])).ToArray();
        BlockingGraph.Build(rows); // warm
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var g = BlockingGraph.Build(rows);
        sw.Stop();
        Assert.Equal([1L], g.HeadBlockers);
        Assert.True(sw.Elapsed.TotalMilliseconds <= TestTime.Budget(5), $"{sw.Elapsed.TotalMilliseconds:0.00} ms");
    }

    // ------------------------------------------------------------------ kill guard

    static KillGuard.Context Ctx(MonitorKillParams p, ActivityRow? row, EnvironmentKind env = EnvironmentKind.Dev, bool connected = true,
        MonitorCapabilities? caps = null, ConnectionRegistry? reg = null, SessionKey? self = null, HashSet<SessionKey>? rolling = null) =>
        new(p, env, connected, row == null ? new Dictionary<SessionKey, ActivityRow>() : new Dictionary<SessionKey, ActivityRow> { [row.Key] = row },
            self ?? K(1, "9:9"), reg ?? new ConnectionRegistry(),
            caps ?? new MonitorCapabilities { ServerVersion = "x", Panes = [], CanTerminate = true, CanCancelRequest = true }, rolling ?? []);

    static MonitorKillParams Kill(SessionKey target, KillMode mode = KillMode.TerminateSession, bool confirmed = true, bool prod = false, string? expected = null) =>
        new() { MonitorId = "m", Target = target, Mode = mode, ConfirmedKill = confirmed, ConfirmedProd = prod, ExpectedStatementStart = expected };

    [Fact]
    public void K1_K2_K3_K4_confirmations_and_snapshot_membership_are_checked_in_order()
    {
        var row = Row(55);
        Assert.Equal("notConfirmed", KillGuard.Check(Ctx(Kill(row.Key, confirmed: false), row))!.Code);
        var prod = KillGuard.Check(Ctx(Kill(row.Key), row, EnvironmentKind.Prod))!;
        Assert.Equal("prod", prod.Code);
        Assert.Equal(GuardReason.ProdWrite, prod.Guard);
        Assert.Null(KillGuard.Check(Ctx(Kill(row.Key, prod: true), row, EnvironmentKind.Prod)));
        Assert.Equal("notInSnapshot", KillGuard.Check(Ctx(Kill(K(56)), row))!.Code);
        Assert.Equal("notInSnapshot", KillGuard.Check(Ctx(Kill(K(55, "1:2")), row))!.Code); // same spid, new login_time
    }

    [Fact]
    public void K6_K11_K14_K21_K29_own_monitor_system_sessions_pseudo_blockers_disconnected_and_hidden_identity_are_refused()
    {
        var self = Row(7);
        Assert.Equal("ownMonitor", KillGuard.Check(Ctx(Kill(self.Key), self, self: self.Key))!.Code);
        var sys = Row(80, user: false); // a system spid above 50 (B0 row 2)
        Assert.Equal("systemSession", KillGuard.Check(Ctx(Kill(sys.Key), sys))!.Code);
        var low = Row(40); // a user session with spid ≤ 50 is not refused for that reason
        Assert.Null(KillGuard.Check(Ctx(Kill(low.Key), low)));
        Assert.Equal("notASession", KillGuard.Check(Ctx(Kill(K(-2)), null))!.Code);
        Assert.Equal("disconnected", KillGuard.Check(Ctx(Kill(low.Key), low, connected: false))!.Code);
        var hidden = Row(90, inst: "") with { IdentityHidden = "requires pg_read_all_stats" };
        Assert.Equal("identityHidden", KillGuard.Check(Ctx(Kill(hidden.Key), hidden))!.Code);
    }

    [Fact]
    public void K7_K8_a_connection_registered_after_the_snapshot_is_refused_at_kill_time()
    {
        var reg = new ConnectionRegistry();
        var row = Row(60) with { QuerithmKind = null }; // looked foreign in the dialog
        var conn = new Fakes.FakeDbConnection((_, _) => Task.FromResult(Fakes.FakeResult.None), 60);
        reg.Register(conn, row.Key, ConnectionRegistry.Kinds.Pool, "t");
        var r = KillGuard.Check(Ctx(Kill(row.Key), row, reg: reg))!;
        Assert.Equal("querithmConnection", r.Code);
        Assert.Equal(KillGuard.QuerithmConnectionMessage, r.Message);
        GC.KeepAlive(conn);
    }

    [Fact]
    public void K10_K26_K27_cancel_rules_engine_reason_statement_identity_and_idle_sessions()
    {
        var ms = new MonitorCapabilities { ServerVersion = "x", Panes = [], CanTerminate = true, CanCancelRequest = false, CancelReason = "SQL Server can only terminate the whole session" };
        var running = Row(70, "running");
        Assert.Equal("unsupported", KillGuard.Check(Ctx(Kill(running.Key, KillMode.CancelRequest), running, caps: ms))!.Code);
        var pg = Row(71, "active") with { StatementStart = "846471094243129" };
        Assert.Equal(KillOutcome.StatementFinished, KillGuard.Check(Ctx(Kill(pg.Key, KillMode.CancelRequest, expected: "846471094243128"), pg))!.Outcome);
        Assert.Null(KillGuard.Check(Ctx(Kill(pg.Key, KillMode.CancelRequest, expected: "846471094243129"), pg)));
        var idle = Row(72, "idle in transaction");
        Assert.Equal(KillOutcome.NoRunningQuery, KillGuard.Check(Ctx(Kill(idle.Key, KillMode.CancelRequest, expected: "1"), idle))!.Outcome);
    }

    [Fact]
    public void K19_a_session_rolling_back_is_refused()
    {
        var row = Row(80);
        Assert.Equal("rollingBack", KillGuard.Check(Ctx(Kill(row.Key), row, rolling: [row.Key]))!.Code);
        Assert.Equal("rollingBack", KillGuard.Check(Ctx(Kill(row.Key), row with { RollingBack = true }))!.Code);
    }

    [Fact]
    public void K12_shapes_are_rejected_at_the_rpc_boundary()
    {
        var ms = new SqlServerProvider().Activity!;
        var pg = new PostgresProvider().Activity!;
        Assert.NotNull(KillGuard.ValidateShape(Kill(K(0)), ms, Engine.Mssql));
        Assert.NotNull(KillGuard.ValidateShape(Kill(K(32_768)), ms, Engine.Mssql));
        Assert.NotNull(KillGuard.ValidateShape(Kill(K(5, new string('1', 65))), ms, Engine.Mssql));
        Assert.NotNull(KillGuard.ValidateShape(Kill(K(5, "abc")), ms, Engine.Mssql));
        Assert.NotNull(KillGuard.ValidateShape(Kill(K(5, "46321:3076143"), KillMode.CancelRequest, expected: "1:1"), ms, Engine.Mssql));
        Assert.Null(KillGuard.ValidateShape(Kill(K(5, "46321:3076143")), ms, Engine.Mssql));
        Assert.NotNull(KillGuard.ValidateShape(Kill(K(5, "846471094243129"), KillMode.CancelRequest), pg, Engine.Pg)); // PG cancel needs the statement start
        Assert.NotNull(KillGuard.ValidateShape(Kill(K(5, "-5")), pg, Engine.Pg));
        Assert.Null(KillGuard.ValidateShape(Kill(K(5, "846471094243129"), KillMode.CancelRequest, expected: "846471094243130"), pg, Engine.Pg));
    }

    [Fact]
    public void K13_instance_round_trips_exactly_per_engine()
    {
        var login = new DateTime(2026, 10, 28, 2, 50, 53, 813, DateTimeKind.Unspecified).AddTicks(3_333); // 1/300 s precision
        var sqlDt = new System.Data.SqlTypes.SqlDateTime(login);
        var inst = SqlServerActivity.FormatInstance(sqlDt.Value);
        Assert.True(SqlServerActivity.TryParseInstance(inst, out var back));
        Assert.Equal(sqlDt.DayTicks, back.DayTicks);
        Assert.Equal(sqlDt.TimeTicks, back.TimeTicks);
        var start = new DateTime(2026, 10, 28, 2, 51, 34, DateTimeKind.Utc).AddTicks(2_431_290);
        var us = PostgresActivity.FormatMicros(start);
        Assert.True(PostgresActivity.TryParseMicros(us, out var pgBack));
        Assert.Equal(start, pgBack);
        Assert.True(PostgresActivity.TryParseMicros((long.Parse(us) + 1).ToString(), out var off));
        Assert.NotEqual(start, off);
    }

    [Fact]
    public void K22_the_kill_batch_formats_only_a_validated_integer()
    {
        Assert.Equal("IF EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE session_id = @id AND login_time = @start AND is_user_process = 1) KILL 53; ELSE SELECT 'gone';",
            SqlServerActivitySessionAccess.TerminateSql(53));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlServerActivitySessionAccess.TerminateSql(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlServerActivitySessionAccess.TerminateSql(40_000));
    }

    [Fact]
    public void P1_P6_P7_P8_rollback_risk_thresholds()
    {
        KillPreview P(long? log = null, long? txMs = null, int? tx = 1, bool? writes = null, RollbackRisk risk = RollbackRisk.None) =>
            new() { Key = K(1), LogBytesUsed = log, TxElapsedMs = txMs, TxCount = tx, HasWrites = writes, Risk = risk };
        Assert.Equal(RollbackRisk.None, KillRisk.Classify(P(tx: 0)));                                // P1
        Assert.Equal(RollbackRisk.Unknown, KillRisk.Classify(P(risk: RollbackRisk.Unknown)));         // P6
        Assert.True(KillRisk.NeedsLongRollbackConfirm(RollbackRisk.Unknown));
        Assert.Equal(RollbackRisk.Low, KillRisk.Classify(P(log: KillRisk.HighLogBytes - 1, txMs: KillRisk.HighTxMs - 1))); // P7 below
        Assert.Equal(RollbackRisk.High, KillRisk.Classify(P(log: KillRisk.HighLogBytes)));            // at
        Assert.Equal(RollbackRisk.High, KillRisk.Classify(P(txMs: KillRisk.HighTxMs)));
        Assert.Equal(RollbackRisk.High, KillRisk.Classify(P(log: KillRisk.HighLogBytes + 1)));        // above
        Assert.Equal(RollbackRisk.Low, KillRisk.Classify(P(txMs: 5 * 60_000)));                        // the plan's 1–10 min gap is low
        Assert.Equal(RollbackRisk.Low, KillRisk.Classify(P(txMs: 24 * 3_600_000, writes: false)));     // P8: read-only PG tx
        Assert.Equal(RollbackRisk.High, KillRisk.Classify(P(txMs: KillRisk.HighTxMs, writes: true)));
        Assert.Equal(100L * 1024 * 1024, KillRisk.HighLogBytes);                                      // constants pinned
        Assert.Equal(600_000, KillRisk.HighTxMs);
    }

    // ------------------------------------------------------------------ history

    static MonitorSnapshot Snap(long seq, params ActivityRow[] rows) => new() { MonitorId = "m", Seq = seq, At = seq * 2_000, Sessions = rows, Status = [] };

    [Fact]
    public void H1_H3_H4_diffs_replay_to_the_original_oldest_go_first_and_gaps_are_marked()
    {
        var texts = new TextStore();
        var h = new HistoryBuffer(texts, retentionMinutes: 1);
        var originals = new List<MonitorSnapshot>();
        for (var i = 1; i <= 80; i++)
        {
            var rows = Enumerable.Range(1, 5).Where(s => (s + i) % 7 != 0).Select(s => Row(s, i % 3 == 0 ? "sleeping" : "running", s == 2 && i % 4 == 0 ? [1] : [])).ToArray();
            // Two reconnect gaps: one that retention evicts with its tick, one inside the retained minute (ticks 50–80).
            if (i is 40 or 70) h.MarkGap();
            var snap = Snap(i, rows);
            originals.Add(snap);
            h.Add(snap, i * 2_000L);
        }
        Assert.True(h.Count <= 31); // 1 min at 2 s (H1)
        var ticks = h.Ticks();
        Assert.Equal(originals[^ticks.Length].Seq, ticks[0].Seq);
        Assert.True(ticks[0].Seq > 40, $"oldest retained tick {ticks[0].Seq}"); // the first gap went with its tick
        foreach (var t in ticks)
        {
            var rebuilt = h.At(t.Seq)!;
            var original = originals.Single(o => o.Seq == t.Seq);
            Assert.Equal(Canon(original.Sessions!), Canon(rebuilt.Sessions!)); // H3
        }
        // H4: exactly the tick after the gap is marked (never interpolated), and the mark survives keyframe rewrites
        // as older ticks are evicted.
        Assert.Equal(70, Assert.Single(ticks, t => t.GapBefore).Seq);
    }

    static string Canon(ActivityRow[] rows) => JsonSerializer.Serialize(rows.OrderBy(r => r.Key.Id).ToArray(), Json.Options);

    [Fact]
    public void H2_the_memory_cap_evicts_oldest_and_says_so()
    {
        // Ticks are stored compressed (a few KB for 50 new sessions), so the cap is set to a few dozen ticks.
        var h = new HistoryBuffer(new TextStore(), retentionMinutes: 60, capBytes: 40_000);
        for (var i = 1; i <= 200; i++) h.Add(Snap(i, Enumerable.Range(1, 50).Select(s => Row(s * 1000 + i)).ToArray()), i * 2_000L);
        Assert.True(h.CapHit);
        Assert.True(h.Bytes <= 40_000, $"{h.Bytes} bytes held");
        Assert.True(h.Count is > 1 and < 200, $"{h.Count} ticks kept");
        Assert.Equal(200, h.Ticks()[^1].Seq);
    }

    // ------------------------------------------------------------------ top queries

    static TopQueryCounter Q(string entry, string hash, long execs, double dur, double? cpu = null) =>
        new(TopQueryKey.Of(entry), hash, null, "t:" + entry, execs, cpu ?? dur, dur, 10 * execs, null, execs);

    [Fact]
    public void T1_T2_T5_T7_deltas_per_entry_grouped_by_hash_evictions_dropped_and_resets_detected()
    {
        var t = new TopQueriesTracker();
        var (s0, _) = t.Update(new TopQueryRead([Q("p1", "h1", 10, 100), Q("p2", "h1", 5, 50), Q("p3", "h2", 1, 10)], true, null), "cpu", false);
        Assert.Equal(PaneState.Collecting, s0);
        // Incremental read: only p1 changed (T7). p2 and p3 are unchanged, not evicted.
        var (s1, top1) = t.Update(new TopQueryRead([Q("p1", "h1", 12, 130)], false, null), "cpu", false);
        Assert.Equal(PaneState.Ok, s1);
        var h1 = Assert.Single(top1);
        Assert.Equal(2, h1.Execs);
        Assert.Equal(30, h1.DurationMs);
        // Full read without p2 (evicted, T2): dropped, no negative delta; p3 re-created with lower counters counts from zero.
        var (_, top2) = t.Update(new TopQueryRead([Q("p1", "h1", 12, 130), Q("p3", "h2", 1, 4)], true, null), "cpu", false);
        Assert.Equal("h2", Assert.Single(top2).QueryHash);
        Assert.All(top2, x => Assert.True(x.Execs >= 0 && x.DurationMs >= 0));
        // T5: two entries with the same hash (different literals) are grouped, one sample text.
        var (_, top3) = t.Update(new TopQueryRead([Q("p1", "h1", 15, 160), Q("p9", "h1", 2, 500)], false, null), "duration", false);
        var g = Assert.Single(top3);
        Assert.Equal(5, g.Execs);
        Assert.Equal(TopQueryKey.Of("p9"), g.Sample!.Entry);
        // T1: pg_stat_statements_reset → marker changed → reset detected.
        t.Update(new TopQueryRead([Q("p1", "h1", 15, 160)], true, "r1"), "cpu", false);
        var (s4, top4) = t.Update(new TopQueryRead([Q("p1", "h1", 1, 1)], true, "r2"), "cpu", false);
        Assert.Equal(PaneState.ResetDetected, s4);
        Assert.Empty(top4);
    }

    [Fact]
    public void T9_a_query_that_completes_between_polls_appears_in_the_next_incremental_read()
    {
        // Fake rows: run N's read did not include the entry (still executing); run N+1's read (cutoff = earliest running
        // start of run N) includes it with exec delta 1.
        var t = new TopQueriesTracker();
        t.Update(new TopQueryRead([Q("p1", "h1", 3, 30)], true, null), "cpu", false);
        t.Update(new TopQueryRead([], false, null), "cpu", false);
        var (_, top) = t.Update(new TopQueryRead([Q("p1", "h1", 4, 9_030)], false, null), "cpu", false);
        Assert.Equal(1, Assert.Single(top).Execs);
    }

    // ------------------------------------------------------------------ SQL Server parsers

    [Fact]
    public void D12_D17_cpu_from_the_ring_buffer_is_read_defensively()
    {
        Assert.Equal(17, SqlServerActivitySessionAccess.Cpu("<Record id=\"3\"><SchedulerMonitorEvent><SystemHealth><ProcessUtilization>17</ProcessUtilization><SystemIdle>0</SystemIdle></SystemHealth></SchedulerMonitorEvent></Record>").Value);
        var empty = SqlServerActivitySessionAccess.Cpu(null);
        Assert.Null(empty.Value);
        Assert.Contains("n/a", empty.Note);
        var odd = SqlServerActivitySessionAccess.Cpu("<Record><Other>1</Other>");
        Assert.Null(odd.Value);
        Assert.Equal("n/a (ring buffer format)", odd.Note);
    }

    internal const string Deadlock = """
        <RingBufferTarget><event name="xml_deadlock_report" package="sqlserver" timestamp="2026-10-28T03:00:00.000Z"><data name="xml_report"><value>
        <deadlock><victim-list><victimProcess id="process1"/></victim-list>
        <process-list>
          <process id="process1" spid="55" loginname="app" hostname="h1" clientapp="App &lt;b&gt;" currentdb="5" currentdbname="SalesDB" isolationlevel="read committed (2)" lockMode="U" waitresource="KEY: 5:1 (a)" waittime="1200"><executionStack><frame procname="adhoc">UPDATE t SET v = 1 WHERE id = 2</frame></executionStack><inputbuf>BEGIN TRAN</inputbuf></process>
          <process id="process2" spid="56" loginname="app" currentdb="5" currentdbname="SalesDB" lockMode="U"><inputbuf>UPDATE t SET v = 2 WHERE id = 1</inputbuf></process>
        </process-list>
        <resource-list><keylock hobtid="1" dbid="5" objectname="SalesDB.dbo.t" indexname="PK_t" id="lock1" mode="X"><owner-list><owner id="process2" mode="X"/></owner-list><waiter-list><waiter id="process1" mode="U" requestType="wait"/></waiter-list></keylock>
        <exchangeEvent id="Pipe1" WaitType="e_waitPipeGetRow"><owner-list><owner id="process2"/></owner-list><waiter-list><waiter id="process1"/></waiter-list></exchangeEvent></resource-list></deadlock>
        </value></data></event></RingBufferTarget>
        """;

    [Fact]
    public void X2_X6_deadlock_graphs_parse_with_victims_resources_and_exchange_events_and_dedupe_across_sources()
    {
        var ring = SqlServerDeadlocks.Parse(Deadlock, "ring buffer").ToList();
        var ev = Assert.Single(ring);
        Assert.Equal("process1", ev.Victims.Single().ProcessId);
        Assert.True(ev.Processes.Single(p => p.Id == "process1").Victim);
        Assert.Equal("App <b>", ev.Processes.Single(p => p.Id == "process1").Program); // data, not markup (W7)
        Assert.Equal("UPDATE t SET v = 1 WHERE id = 2", ev.Processes.Single(p => p.Id == "process1").Text);
        Assert.Contains(ev.Resources, r => r.Kind == "keylock" && r.Object == "SalesDB.dbo.t" && r.Database == "SalesDB" && r.Owners.Length == 2);
        Assert.Contains(ev.Resources, r => r.Kind == "exchangeEvent");
        var file = SqlServerDeadlocks.Parse(Deadlock.Replace("<RingBufferTarget>", "").Replace("</RingBufferTarget>", ""), "event file").Single();
        Assert.Equal(ev.Id, file.Id); // X2: same graph, same id
        Assert.False(ev.Partial);
    }

    [Fact]
    public void X3_X4_X5_cut_off_and_dtd_xml_and_oversized_events()
    {
        var cut = Deadlock[..Deadlock.IndexOf("<resource-list>", StringComparison.Ordinal)];
        var partial = Assert.Single(SqlServerDeadlocks.Parse(cut, "ring buffer"));
        Assert.True(partial.Partial);
        Assert.Equal(2, partial.Processes.Length);
        var dtd = "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]>" + Deadlock.Replace("BEGIN TRAN", "&e;");
        Assert.Empty(SqlServerDeadlocks.Parse(dtd, "ring buffer")); // refused before any entity is resolved
        var big = Deadlock.Replace("BEGIN TRAN", new string('x', SqlServerDeadlocks.MaxEventChars + 10));
        Assert.Empty(SqlServerDeadlocks.Parse(big, "ring buffer"));
        Assert.Empty(SqlServerDeadlocks.Parse("", "ring buffer")); // X12
    }

    // ------------------------------------------------------------------ capabilities from fake probes (D16, D18, K15)

    static SqlServerActivity.Probe MsProbe(int edition = 3, bool vss = true, bool vsps = true, bool aac = true, bool vds = true, bool kdc = true) =>
        new(edition, 16, "16.0.4305.5", vss, vsps, aac, vds, kdc, true, true, "master");

    [Fact]
    public void D16_K15_azure_sql_database_scopes_panes_and_names_its_own_permission()
    {
        var c = SqlServerActivity.Capabilities(MsProbe(edition: 5, vss: false, vsps: false, aac: false, vds: true, kdc: false));
        Assert.True(c.AllSessionsVisible);
        Assert.False(c.Panes.Single(p => p.Pane == MonitorPane.Waits).Available);
        Assert.False(c.Panes.Single(p => p.Pane == MonitorPane.Deadlocks).Available);
        Assert.False(c.CanTerminate);
        Assert.Contains("KILL DATABASE CONNECTION", c.TerminateReason);
        Assert.Equal("Azure SQL Database", c.Platform);
    }

    [Fact]
    public void D18_managed_servers_degrade_from_probed_capabilities_not_platform_names()
    {
        var mi = SqlServerActivity.Capabilities(MsProbe(edition: 8, vss: false, vsps: true, aac: false));
        Assert.True(mi.AllSessionsVisible);
        Assert.Contains("ALTER ANY CONNECTION", mi.TerminateReason);
        PostgresActivity.Probe Pg(bool su = false, bool ras = true, bool sig = true, string? pss = "public", string? pssUn = null, string? pws = null, bool io = true) =>
            new(160000, "PostgreSQL 16.4 on x", su, ras, sig, false, false, 1024, "auto", io, io, false, true, true, pss, pssUn, ["calls", "total_exec_time", "rows", "shared_blks_hit", "shared_blks_read", "blk_read_time", "blk_write_time"], true, pws, null, null, "rds_admin", "postgres");
        var rds = PostgresActivity.Capabilities(Pg());
        Assert.True(rds.AllSessionsVisible);
        Assert.Equal("Superuser sessions can only be signalled by a superuser.", rds.TerminateReason);
        Assert.Equal("tick-sampled", PostgresActivity.WaitSource(Pg()));
        Assert.Contains("low resolution", rds.Panes.Single(p => p.Pane == MonitorPane.Waits).Reason);
        var noPss = PostgresActivity.Capabilities(Pg(pss: null));
        Assert.Contains("not installed", noPss.Panes.Single(p => p.Pane == MonitorPane.TopQueries).Reason);
        var notLoaded = PostgresActivity.Capabilities(Pg(pssUn: "pg_stat_statements is installed but not loaded (add it to shared_preload_libraries and restart)."));
        Assert.Contains("shared_preload_libraries", notLoaded.Panes.Single(p => p.Pane == MonitorPane.TopQueries).Reason);
        var noStats = PostgresActivity.Capabilities(Pg(ras: false, sig: false));
        Assert.False(noStats.Panes.Single(p => p.Pane == MonitorPane.Waits).Available);
        Assert.Contains("pg_signal_backend", noStats.OtherLoginsReason);
        var pg15 = PostgresActivity.Capabilities(Pg(io: false));
        Assert.Contains("PostgreSQL 16", pg15.Panes.Single(p => p.Pane == MonitorPane.Io).Reason);
        var qid = PostgresActivity.Capabilities(Pg(pss: null) with { ComputeQueryId = "auto" });
        Assert.Contains("compute_query_id", qid.QueryHashReason); // D19
    }

    [Fact]
    public void D6_the_benign_wait_list_is_fixed_and_benign_waits_are_never_shown()
    {
        Assert.Equal(79, SqlServerWaits.Benign.Count);
        Assert.Contains("SLEEP_TASK", SqlServerWaits.Benign);
        Assert.DoesNotContain("LCK_M_X", SqlServerWaits.Benign);
        Assert.Equal("Lock", SqlServerWaits.Category("LCK_M_SCH_M"));
        Assert.Equal("Buffer IO", SqlServerWaits.Category("PAGEIOLATCH_SH"));
        Assert.Equal("Parallelism", SqlServerWaits.Category("CXCONSUMER"));
        Assert.True(PostgresWaits.IsBenign("Activity", "WalWriterMain"));
        Assert.True(PostgresWaits.IsBenign("Client", "ClientRead"));
        Assert.False(PostgresWaits.IsBenign("Lock", "relation"));
        Assert.Equal("CPU", PostgresWaits.Category(null, null));
    }

    [Fact]
    public void Panes_are_distinct_names_and_reserved_ones_are_refused()
    {
        var names = Enum.GetNames<MonitorPane>();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Contains("QueryStore", names);
        Assert.Contains("Health", names);
    }
}

/// <summary>Reaches the provider's internal SQL builders and parsers for assertions.</summary>
static class SqlServerActivitySessionAccess
{
    public static string TerminateSql(long id) => SqlServerActivitySession.TerminateSql(id);
    public static UInt128 EntryKey(byte[] planHandle, int start, int end) => SqlServerActivitySession.EntryKey(planHandle, start, end);
    public static MetricCounter Cpu(string? record) => SqlServerActivitySession.CpuFromRingBuffer(record);

    public static IActivitySession Session(System.Data.Common.DbConnection c)
    {
        var probe = new SqlServerActivity.Probe(3, 16, "16.0", true, true, true, true, true, true, true, "master");
        return new SqlServerActivitySession(probe, SqlServerActivity.Capabilities(probe), new SessionKey { Id = 1, Instance = "1:1" }, new SqlServerPlans(), new SqlServerDataEdit());
    }
}
