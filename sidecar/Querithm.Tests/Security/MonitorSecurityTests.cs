using System.Data;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Monitoring;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 for the activity monitor (Phase B rows typed S): the sidecar refuses unsafe kills and malformed input before any SQL
/// is built, builds SQL only from validated integers and typed parameters, treats server-returned names as data, keeps
/// exports in app data without user data unless asked, and never changes an event session.
/// </summary>
public class MonitorSecurityTests
{
    static async Task<(MonitorService Service, string MonitorId, FakeActivity Activity, FakeMonitorClock Clock, SessionManager Sessions, FakeDbProvider Provider)> Running(EnvironmentKind env, params ActivityRow[] rows)
    {
        var provider = new FakeDbProvider();
        var activity = new FakeActivity { NewSession = () => { var s = new FakeActivitySession(); s.Rows.AddRange(rows); return s; } };
        provider.Activity = activity;
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "p", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u", Environment = env });
        var sessions = new SessionManager(profiles, new ProviderRegistry([provider]), new CapturingNotifier(), Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var clock = new FakeMonitorClock();
        var service = new MonitorService(sessions, new CapturingNotifier(), Log.Null, Directory.CreateTempSubdirectory("qsec-").FullName, null, clock) { InlineSchedulers = true };
        var id = service.Start(new MonitorStartParams { ProfileId = profile.Id, IntervalMs = 1_000 }).MonitorId;
        clock.Advance(10);
        await Task.Yield();
        return (service, id, activity, clock, sessions, provider);
    }

    static ActivityRow Row(long id, bool user = true) => new() { Key = new SessionKey { Id = id, Instance = FakeActivity.Inst(id) }, Status = "sleeping", BlockedBy = [], IsUserSession = user };

    static MonitorKillParams Kill(string id, long target, bool confirmed = true, bool prod = false, string? instance = null) => new()
    {
        MonitorId = id, Target = new SessionKey { Id = target, Instance = instance ?? FakeActivity.Inst(target) }, Mode = KillMode.TerminateSession,
        ConfirmedKill = confirmed, ConfirmedProd = prod, ConfirmedLongRollback = true,
    };

    [Fact]
    public Task K1_K2_K3_K4_K6_K11_K14_K16_refusals_send_nothing_to_the_server() => Task.Run(async () =>
    {
        var (svc, id, activity, _, _, _) = await Running(EnvironmentKind.Prod, Row(55), Row(80, user: false));
        var self = activity.Session!.Self;
        var cases = new (MonitorKillParams P, string Code)[]
        {
            (Kill(id, 55, confirmed: false), "notConfirmed"),                    // K1
            (Kill(id, 55, prod: false), "prod"),                                 // K2
            (Kill(id, 56, prod: true), "notInSnapshot"),                         // K3
            (Kill(id, 55, prod: true, instance: "1:9999"), "notInSnapshot"),     // K4: same spid, other start time
            (Kill(id, self.Id, prod: true), "ownMonitor"),                       // K6
            (Kill(id, 80, prod: true), "systemSession"),                         // K11
            (Kill(id, -2, prod: true, instance: ""), "notASession"),             // K14
        };
        foreach (var (p, code) in cases)
        {
            var r = await svc.KillAsync(p);
            Assert.Equal(KillOutcome.Refused, r.Outcome);
            Assert.Equal(code, r.Code);
        }
        Assert.Equal(GuardReason.ProdWrite, (await svc.KillAsync(Kill(id, 55))).Guard);
        Assert.Equal(0, activity.Session.Terminates);
        Assert.DoesNotContain(activity.Calls, c => c.StartsWith("terminate", StringComparison.Ordinal));
        Assert.Equal("notFound", (await Assert.ThrowsAsync<QuerithmException>(() => svc.KillAsync(Kill("01HZZZZZZZZZZZZZZZZZZZZZZZ", 55)))).Code); // K16
        await svc.DisposeAsync();
    });

    [Fact]
    public Task K7_a_querithm_connection_is_refused_with_its_reason_and_nothing_is_sent() => Task.Run(async () =>
    {
        var (svc, id, activity, clock, sessions, provider) = await Running(EnvironmentKind.Dev);
        var tab = sessions.Get(sessions.Open(new SessionOpenParams { ProfileId = svc.All.First().Profile.Id, Secret = "x" }).SessionId);
        await sessions.TxAsync(tab.Id, "begin", CancellationToken.None);
        var key = sessions.Registry.KeyOf(tab.Pinned!.Db)!;
        activity.Session!.Rows.Add(new ActivityRow { Key = key, Status = "sleeping", BlockedBy = [], IsUserSession = true });
        clock.Advance(1_000);
        var r = await svc.KillAsync(new MonitorKillParams { MonitorId = id, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
        Assert.Equal("querithmConnection", r.Code);
        Assert.Equal(KillGuard.QuerithmConnectionMessage, r.Message);
        Assert.Equal(0, activity.Session.Terminates);
        _ = provider;
        await svc.DisposeAsync();
    });

    [Fact]
    public Task K7_while_a_querithm_connection_the_server_would_not_identify_is_open_every_kill_is_refused_and_nothing_is_sent() => Task.Run(async () =>
    {
        // §6 rule 7 (revised): the connection serves queries, but its server session looks foreign — it may be the target.
        var (svc, id, activity, clock, sessions, _) = await Running(EnvironmentKind.Dev, Row(55));
        activity.OnIdentity = (_, _) => throw new IdentityUnavailableException("refused (test)");
        var tab = sessions.Get(sessions.Open(new SessionOpenParams { ProfileId = svc.All.First().Profile.Id, Secret = "x" }).SessionId);
        await sessions.TxAsync(tab.Id, "begin", CancellationToken.None);
        activity.OnIdentity = null;
        Assert.Null(sessions.Registry.KeyOf(tab.Pinned!.Db));
        // Its server session (spid as the fake reports it) shows as a foreign row: the kill would hit the tab's own transaction.
        var spid = ((FakeDbConnection)tab.Pinned.Db).Spid;
        activity.Session!.Rows.Add(Row(spid));
        clock.Advance(1_000);
        foreach (var target in new[] { spid, 55L })
        {
            var r = await svc.KillAsync(new MonitorKillParams { MonitorId = id, Target = Row(target).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
            Assert.Equal((KillOutcome.Refused, "querithmUnidentified"), (r.Outcome, r.Code));
            Assert.Contains("refused (test)", r.Message);
        }
        Assert.Equal(0, activity.Session.Terminates);
        Assert.DoesNotContain(activity.Calls, c => c.StartsWith("terminate", StringComparison.Ordinal));
        await svc.DisposeAsync();
    });

    [Fact]
    public Task K12_L14_M12_malformed_input_is_rejected_at_the_rpc_boundary() => Task.Run(async () =>
    {
        var (svc, id, _, _, _, _) = await Running(EnvironmentKind.Dev, Row(55));
        foreach (var bad in new[] { 0L, 32_768L, long.MaxValue })
            Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => svc.KillAsync(Kill(id, bad)))).Code);
        foreach (var inst in new[] { new string('1', 65), "not:a:number", "abc" })
            Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => svc.KillAsync(Kill(id, 55, instance: inst)))).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => svc.Start(new MonitorStartParams { ProfileId = "p", IntervalMs = 500 })).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => svc.Texts(new MonitorTextsParams { MonitorId = id, Ids = ["zz"] })).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => svc.SetPanes(new MonitorSetPanesParams { MonitorId = id, Panes = [MonitorPane.Health] })).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => svc.DeadlocksAsync(new DeadlocksParams { MonitorId = id, FromMs = 0, ToMs = 9 * 86_400_000L }))).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => svc.HistorySettings(new HistorySettingsParams { MonitorId = id, RetentionMinutes = 61 })).Code);
        await svc.DisposeAsync();
    });

    [Fact]
    public Task L12_more_than_eight_monitors_are_refused() => Task.Run(async () =>
    {
        var (svc, _, _, _, sessions, _) = await Running(EnvironmentKind.Dev);
        var profile = sessions.All.FirstOrDefault()?.Profile.Id ?? svc.All.First().Profile.Id;
        var started = 1;
        QuerithmException? refused = null;
        for (var i = 0; i < 49; i++)
        {
            try { svc.Start(new MonitorStartParams { ProfileId = profile, IntervalMs = 1_000 }); started++; }
            catch (QuerithmException e) { refused = e; }
        }
        Assert.Equal(MonitorService.MaxMonitors, started);
        Assert.Equal("tooManyMonitors", refused!.Code);
        await svc.DisposeAsync();
    });

    [Fact]
    public void K22_the_kill_batch_takes_the_spid_only_as_a_validated_integer_and_the_start_time_as_a_typed_parameter()
    {
        var sql = SqlServerActivitySessionAccess.TerminateSql(53);
        Assert.Contains("KILL 53;", sql);
        Assert.Contains("login_time = @start", sql);
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlServerActivitySessionAccess.TerminateSql(-1));
        Assert.Throws<ArgumentException>(() => SqlServerActivity.StartParam(new SessionKey { Id = 53, Instance = "1; DROP TABLE x" }));
        var p = SqlServerActivity.StartParam(new SessionKey { Id = 53, Instance = "46321:3076143" });
        Assert.Equal(SqlDbType.DateTime, p.SqlDbType);
        Assert.Throws<ArgumentException>(() => PostgresActivity.Ts("start", "1 OR 1=1"));
        Assert.Contains("pid = @pid AND backend_start = @start", PostgresActivitySessionAccess.TerminateSql);
        Assert.Contains("query_start = @qs", PostgresActivitySessionAccess.CancelSql);
    }

    [Fact]
    public async Task S13_server_returned_names_are_quoted_as_identifiers_never_spliced_raw()
    {
        // A database name from sys.dm_tran_locks goes into the lazy name resolver as a three-part name.
        var seen = new List<string>();
        var conn = new FakeDbConnection((cmd, _) => { seen.Add(cmd.CommandText); return Task.FromResult(new FakeResult([new DataTable()])); });
        var session = SqlServerActivitySessionAccess.Session(conn);
        await session.ResolveObjectNamesAsync(conn, [("x]; DROP TABLE t; --", 5L)], CancellationToken.None);
        var sql = Assert.Single(seen);
        Assert.Contains("[x]]; DROP TABLE t; --].sys.partitions", sql);
        Assert.DoesNotContain("x]; DROP TABLE t; --.", sql.Replace("[x]]; DROP TABLE t; --]", ""));
        Assert.Equal("\"we\"\"ird\"", new PostgresDataEdit().Quote("we\"ird"));
    }

    [Fact]
    public void X4_X5_deadlock_xml_with_a_dtd_or_over_5MB_is_refused_without_resolving_anything()
    {
        var xxe = "<!DOCTYPE d [<!ENTITY e SYSTEM \"http://127.0.0.1:1/x\">]><event name=\"xml_deadlock_report\" timestamp=\"2026-10-28T00:00:00Z\"><data><value><deadlock><process-list><process id=\"p\" spid=\"1\"><inputbuf>&e;</inputbuf></process></process-list></deadlock></value></data></event>";
        Assert.Empty(SqlServerDeadlocks.Parse(xxe, "ring buffer"));
        // The entity points at a local port nothing listens on: a resolver would have failed or hung, nothing was attempted.
        Assert.Equal(System.Xml.DtdProcessing.Prohibit, SqlServerDeadlocks.Safe.DtdProcessing);
        var big = "<event name=\"xml_deadlock_report\" timestamp=\"2026-10-28T00:00:00Z\"><data><value><deadlock><process-list><process id=\"p\"><inputbuf>" + new string('a', SqlServerDeadlocks.MaxEventChars) + "</inputbuf></process></process-list></deadlock></value></data></event>";
        Assert.Empty(SqlServerDeadlocks.Parse(big, "ring buffer"));
    }

    [Fact]
    public Task X14_the_xdl_export_needs_the_text_confirm() => Task.Run(async () =>
    {
        var (svc, id, _, _, _, _) = await Running(EnvironmentKind.Dev);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => svc.DeadlockXmlAsync(new DeadlockXmlParams { MonitorId = id, EventId = new string('a', 32), ConfirmedText = false }));
        Assert.Equal("notConfirmed", ex.Code);
        await svc.DisposeAsync();
    });

    [Fact]
    public void X15_monitor_code_never_creates_alters_starts_or_drops_an_event_session()
    {
        // Scoped to the monitor: §11 Trace legitimately creates querithm_trace_* sessions elsewhere.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var files = Directory.GetFiles(Path.Combine(root, "Querithm.Core", "Monitoring"), "*.cs")
            .Concat([Path.Combine(root, "Querithm.Providers.SqlServer", "SqlServerActivity.cs"), Path.Combine(root, "Querithm.Providers.SqlServer", "SqlServerDeadlocks.cs"),
                     Path.Combine(root, "Querithm.Providers.Postgres", "PostgresActivity.cs")]).ToList();
        Assert.True(files.Count >= 10);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f).ToUpperInvariant();
            foreach (var verb in new[] { "CREATE EVENT SESSION", "ALTER EVENT SESSION", "DROP EVENT SESSION", "STATE = START", "STATE=START", "PG_STAT_RESET", "PG_WAIT_SAMPLING_RESET_PROFILE", "PG_STAT_STATEMENTS_RESET", "DBCC SQLPERF", "DBCC FREEPROCCACHE" })
                Assert.False(text.Contains(verb, StringComparison.Ordinal), $"{Path.GetFileName(f)} contains {verb}");
        }
    }

    [Fact]
    public Task K8_a_connection_registered_after_the_snapshot_is_refused_from_the_registry_read_at_kill_time() => Task.Run(async () =>
    {
        var (svc, id, activity, _, sessions, _) = await Running(EnvironmentKind.Dev, Row(77));
        // The snapshot showed 77 as foreign; it becomes one of Querithm's connections before the kill.
        var conn = new FakeDbConnection((_, _) => Task.FromResult(FakeResult.None), 77);
        await conn.OpenAsync();
        sessions.Registry.Register(conn, Row(77).Key, ConnectionRegistry.Kinds.Pool, "t");
        var r = await svc.KillAsync(Kill(id, 77));
        Assert.Equal(("querithmConnection", 0), (r.Code, activity.Session!.Terminates));
        await svc.DisposeAsync();
        GC.KeepAlive(conn);
    });

    [Fact]
    public Task K10_a_cancel_the_engine_cannot_do_is_refused_by_the_guard_with_the_engine_reason_and_the_provider_sends_nothing() => Task.Run(async () =>
    {
        var (svc, id, activity, _, _, _) = await Running(EnvironmentKind.Dev, Row(55));
        var r = await svc.KillAsync(Kill(id, 55) with { Mode = KillMode.CancelRequest });
        Assert.Equal(KillOutcome.Refused, r.Outcome);
        Assert.Equal(activity.Session!.Capabilities.CancelReason, r.Message); // the guard's refusal, not the provider's
        await svc.DisposeAsync();
        // The SQL Server provider itself never builds a statement for it either.
        var commands = 0;
        await using var c = new FakeDbConnection((_, _) => { commands++; return Task.FromResult(FakeResult.None); }, 1);
        await c.OpenAsync();
        var res = await Tests.SqlServerActivitySessionAccess.Session(c).CancelRequestAsync(c, Row(55).Key, null, CancellationToken.None);
        Assert.Equal(KillOutcome.Refused, res.Outcome);
        Assert.Equal(0, commands);
    });

    [Fact]
    public Task K24_every_kill_attempt_allowed_or_refused_is_audited_without_text_login_or_host() => Task.Run(async () =>
    {
        var provider = new FakeDbProvider();
        const string text = "DELETE FROM k24s_salaries WHERE note = 'k24s_text_marker'";
        var activity = new FakeActivity
        {
            NewSession = () =>
            {
                var s = new FakeActivitySession();
                s.Rows.Add(Row(90) with { Login = "alice_secret_login", Host = "secret-host-01", Program = "secret-app", TextId = "t90" });
                return s;
            },
        };
        provider.Activity = ReshapingActivity.WithTexts(activity, new Dictionary<string, string> { ["t90"] = text });
        var profiles = new ProfileStore(LocalStore.InMemory());
        var profile = profiles.Save(new Profile { Id = "", Name = "p", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        var sessions = new SessionManager(profiles, new ProviderRegistry([provider]), new CapturingNotifier(), Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var clock = new FakeMonitorClock();
        var log = new MonitorServiceTests.CapturingLogger();
        var notifier = new CapturingNotifier();
        var svc = new MonitorService(sessions, notifier, log, Directory.CreateTempSubdirectory("qsec-").FullName, null, clock) { InlineSchedulers = true };
        var id = svc.Start(new MonitorStartParams { ProfileId = profile.Id, IntervalMs = 1_000 }).MonitorId;
        clock.Advance(10);
        // The target row really carries text, login and host into the monitor: the audit check below is not vacuous.
        var row = notifier.Of<MonitorSnapshot>().Last(s => s.Sessions != null).Sessions!.Single(r => r.Key.Id == 90);
        Assert.Equal(text, svc.Texts(new MonitorTextsParams { MonitorId = id, Ids = [row.TextId!] }).Texts[row.TextId!]);
        Assert.Equal("alice_secret_login", row.Login);
        await svc.KillAsync(Kill(id, 90, confirmed: false)); // refused
        await svc.KillAsync(Kill(id, 91));                   // refused: not in the snapshot
        await svc.KillAsync(Kill(id, 90));                   // allowed
        var audit = log.Lines.Where(l => l.StartsWith("Monitor kill", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, audit.Count);
        // No log line at all — audit or other — carries the text, its id, the login, host or program.
        Assert.All(log.Lines, l =>
        {
            foreach (var secret in new[] { "k24s_text_marker", "k24s_salaries", row.TextId!, "alice", "secret-host", "secret-app" }) Assert.DoesNotContain(secret, l);
        });
        await svc.DisposeAsync();
    });

    [Fact]
    public Task P3_P4_the_rollback_risk_is_recomputed_on_the_server_at_kill_time_and_high_risk_needs_the_extra_confirm() => Task.Run(async () =>
    {
        var (svc, id, activity, _, _, _) = await Running(EnvironmentKind.Dev, Row(55));
        // The client has nothing to claim: the kill parameters carry no risk; the server's preview decides (P4).
        activity.Session!.Preview = new KillPreview { Key = Row(55).Key, TxCount = 1, LogBytesUsed = KillRisk.HighLogBytes };
        var refused = await svc.KillAsync(Kill(id, 55) with { ConfirmedLongRollback = false });
        Assert.Equal("longRollback", refused.Code); // P3
        Assert.Equal(0, activity.Session.Terminates);
        Assert.Equal(KillOutcome.Done, (await svc.KillAsync(Kill(id, 55))).Outcome);
        Assert.DoesNotContain(typeof(MonitorKillParams).GetProperties(), pi => pi.Name.Contains("Risk", StringComparison.Ordinal));
        await svc.DisposeAsync();
    });

    [Fact]
    public Task H5_H6_an_export_has_no_text_login_or_host_unless_asked_and_a_hostile_profile_name_cannot_leave_the_exports_folder() => Task.Run(async () =>
    {
        var provider = new FakeDbProvider();
        const string text = "SELECT h5s_text_marker FROM h5s_payroll";
        provider.Activity = ReshapingActivity.WithTexts(new FakeActivity
        {
            NewSession = () => { var s = new FakeActivitySession(); s.Rows.Add(Row(42) with { Login = "carol", Host = "laptop-7", Program = "excel", TextId = "t42" }); return s; },
        }, new Dictionary<string, string> { ["t42"] = text });
        var profiles = new ProfileStore(LocalStore.InMemory());
        var profile = profiles.Save(new Profile { Id = "", Name = "..\\..\\evil/../x:y*", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        var sessions = new SessionManager(profiles, new ProviderRegistry([provider]), new CapturingNotifier(), Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var clock = new FakeMonitorClock();
        var data = Directory.CreateTempSubdirectory("qsec-").FullName;
        var notifier = new CapturingNotifier();
        var svc = new MonitorService(sessions, notifier, Log.Null, data, null, clock) { InlineSchedulers = true };
        var id = svc.Start(new MonitorStartParams { ProfileId = profile.Id, IntervalMs = 1_000 }).MonitorId;
        clock.Run(3_000);
        // The retained ticks really name the text (otherwise "no text in the file" proves nothing).
        var textId = notifier.Of<MonitorSnapshot>().Last(s => s.Sessions != null).Sessions!.Single(r => r.Key.Id == 42).TextId;
        Assert.NotNull(textId);
        var file = svc.Export(new MonitorExportParams { MonitorId = id, IncludeText = false });
        Assert.StartsWith(Path.GetFullPath(Path.Combine(data, "exports")) + Path.DirectorySeparatorChar, Path.GetFullPath(file.Path)); // H6
        Assert.DoesNotContain("..", Path.GetFileName(file.Path));
        var json = File.ReadAllText(file.Path);
        foreach (var secret in new[] { "carol", "laptop-7", "excel", "h5s_text_marker", "h5s_payroll", textId! }) Assert.DoesNotContain(secret, json); // H5
        var withText = File.ReadAllText(svc.Export(new MonitorExportParams { MonitorId = id, IncludeText = true }).Path);
        Assert.Contains("h5s_text_marker", withText); // the opt-in path really carries it
        await svc.DisposeAsync();
    });

    [Fact]
    public void W7_diagnosis_evidence_carries_server_names_as_plain_data_and_the_sidecar_adds_no_markup()
    {
        var hostile = "<img src=x onerror=alert(1)>\u202e\n";
        var rows = new[]
        {
            new ActivityRow { Key = new SessionKey { Id = 2, Instance = "1:2" }, Status = "suspended", BlockedBy = [1], IsUserSession = true },
            new ActivityRow { Key = new SessionKey { Id = 1, Instance = "1:1" }, Status = "sleeping", BlockedBy = [], IsUserSession = true, OpenTx = 1, Program = hostile },
        };
        var d = Diagnoser.Run(rows[0].Key, rows[0], rows, null, null, null, Engine.Mssql, WaitDictionary.Load());
        Assert.Contains($"blocker application {hostile}", d.Findings[0].Evidence); // verbatim: the webview renders it as text
        Assert.All(d.Findings, f => Assert.DoesNotContain("<b>", f.Message));
        Assert.DoesNotContain(typeof(DiagnosisFinding).GetProperties(), pi => pi.Name.Contains("Html", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void M4_no_monitor_job_changes_statement_timeout_so_none_has_to_restore_it()
    {
        // M4 has no subject while this holds: the monitor connection sets statement_timeout once at connect (5 s) and
        // jobs are bounded by the scheduler's cancel, never by raising the timeout for one job.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var pg = File.ReadAllText(Path.Combine(root, "Querithm.Providers.Postgres", "PostgresActivity.cs"));
        static int Count(string text, string what) { var n = 0; for (var i = text.IndexOf(what, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(what, i + 1, StringComparison.OrdinalIgnoreCase)) n++; return n; }
        Assert.Equal(1, Count(pg, "SET statement_timeout"));
        Assert.Contains("MonitorSessionSetupSql => \"SET statement_timeout = '5s';", pg);
        Assert.Equal(0, Count(pg, "set_config"));
        Assert.Equal(0, Count(pg, "SET LOCAL"));
        foreach (var f in Directory.GetFiles(Path.Combine(root, "Querithm.Core", "Monitoring"), "*.cs"))
            Assert.Equal(0, Count(File.ReadAllText(f), "statement_timeout"));
    }

    [Fact]
    public void P6_an_unreadable_rollback_cost_needs_the_long_rollback_confirm()
    {
        var p = new KillPreview { Key = new SessionKey { Id = 1, Instance = "1:1" }, Risk = RollbackRisk.Unknown, Reason = "requires VIEW SERVER STATE" };
        Assert.True(KillRisk.NeedsLongRollbackConfirm(KillRisk.Classify(p)));
    }
}

static class PostgresActivitySessionAccess
{
    public static string TerminateSql => PostgresActivitySession.TerminateSql;
    public static string CancelSql => PostgresActivitySession.CancelSql;
}
