using Querithm.Contract;
using Querithm.Core.Monitoring;
using Querithm.Providers.Oracle;

namespace Querithm.Tests;

/// <summary>D4 S12 activity monitor rules that need no server: panes and reasons per grant, wait categories, identities.</summary>
public sealed class OracleActivityRulesTests
{
    static OracleActivity.Probe Probe(bool sessions = true, bool locks = true, bool sql = true, bool stats = true, bool events = true, bool sysstat = true,
        bool tx = true, bool metric = true, bool alter = true) =>
        new("Oracle AI Database 26ai Free 23.26.0.0.0", "SYSTEM", sessions, locks, sql, stats, events, sysstat, tx, metric, Instance: true, alter, 1, 8192);

    static PaneCapability Pane(MonitorCapabilities c, MonitorPane p) => c.Panes.Single(x => x.Pane == p);

    [Fact]
    public void Every_grant_offers_every_pane_oracle_has_and_kills()
    {
        var caps = OracleActivity.Capabilities(Probe());
        Assert.True(caps.CanTerminate && caps.CanCancelRequest && caps.AllSessionsVisible);
        Assert.Null(caps.TerminateReason);
        Assert.Null(caps.QueryHashReason); // SQL_ID groups statements on every server
        foreach (var pane in new[] { MonitorPane.Sessions, MonitorPane.Locks, MonitorPane.Waits, MonitorPane.Metrics, MonitorPane.TopQueries })
            Assert.True(Pane(caps, pane).Available, pane.ToString());
        Assert.False(Pane(caps, MonitorPane.Io).Available);
        Assert.Contains("Oracle", Pane(caps, MonitorPane.Io).Reason);
        Assert.False(Pane(caps, MonitorPane.Deadlocks).Available);
        Assert.Contains("ORA-00060", Pane(caps, MonitorPane.Deadlocks).Reason);
        Assert.False(caps.LivePlans);
        Assert.NotNull(caps.LivePlanReason);
        Assert.Null(OracleActivity.StartRefusal(Probe()));
    }

    [Fact]
    public void Without_alter_system_sessions_are_shown_and_kills_say_which_privilege_they_need()
    {
        var caps = OracleActivity.Capabilities(Probe(alter: false));
        Assert.False(caps.CanTerminate || caps.CanCancelRequest);
        Assert.Contains("ALTER SYSTEM", caps.TerminateReason);
        Assert.Contains("ALTER SYSTEM", caps.CancelReason);
        Assert.True(Pane(caps, MonitorPane.Sessions).Available);
    }

    [Theory]
    [InlineData("locks", MonitorPane.Locks, "V_$LOCK")]
    [InlineData("stats", MonitorPane.TopQueries, "V_$SQLSTATS")]
    [InlineData("events", MonitorPane.Waits, "V_$SYSTEM_EVENT")]
    public void A_view_the_account_cannot_read_turns_its_pane_off_with_the_grant(string missing, MonitorPane pane, string grant)
    {
        var caps = OracleActivity.Capabilities(Probe(locks: missing != "locks", stats: missing != "stats", events: missing != "events"));
        Assert.False(Pane(caps, pane).Available);
        Assert.Contains(grant, Pane(caps, pane).Reason);
        Assert.Contains("SELECT_CATALOG_ROLE", Pane(caps, pane).Reason);
    }

    [Fact]
    public void Missing_text_rates_or_cpu_views_keep_their_panes_and_say_what_is_missing()
    {
        Assert.Contains("V_$SQL ", OracleActivity.Capabilities(Probe(sql: false)).Panes.Single(p => p.Pane == MonitorPane.Sessions).Reason + " ");
        var noStat = Pane(OracleActivity.Capabilities(Probe(sysstat: false)), MonitorPane.Metrics);
        Assert.True(noStat.Available);
        Assert.Contains("V_$SYSSTAT", noStat.Reason);
        Assert.Contains("V_$SYSMETRIC", Pane(OracleActivity.Capabilities(Probe(metric: false)), MonitorPane.Metrics).Reason);
    }

    [Fact]
    public void Without_v_session_the_monitor_refuses_to_start_naming_the_grant()
    {
        var refusal = OracleActivity.StartRefusal(Probe(sessions: false));
        Assert.Contains("V_$SESSION", refusal);
        Assert.Contains("SELECT_CATALOG_ROLE", refusal);
        var caps = OracleActivity.Capabilities(Probe(sessions: false));
        Assert.False(caps.AllSessionsVisible || caps.CanTerminate || caps.CanCancelRequest);
        Assert.Contains("V_$SESSION", OracleActivity.NoSessionGrant);
    }

    [Theory]
    [InlineData("enq: TX - row lock contention", null, "Lock")]
    [InlineData("enq: TM - contention", null, "Lock")]
    [InlineData("enq: UL - contention", null, "Lock")]
    [InlineData("enq: HW - contention", "Configuration", "Other")]
    [InlineData("latch: cache buffers chains", null, "Latch")]
    [InlineData("library cache lock", null, "Latch")]
    [InlineData("cursor: pin S wait on X", null, "Latch")]
    [InlineData("buffer busy waits", null, "Latch")]
    [InlineData("log file sync", "Commit", "Log IO")]
    [InlineData("log file parallel write", "System I/O", "Log IO")]
    [InlineData("db file sequential read", "User I/O", "Buffer IO")]
    [InlineData("direct path read", null, "Buffer IO")]
    [InlineData("control file parallel write", "System I/O", "Buffer IO")]
    [InlineData("SQL*Net more data to client", "Network", "Network")]
    [InlineData("resmgr:cpu quantum", "Scheduler", "CPU")]
    [InlineData("Allocate UGA memory from OS", "Other", "Other")]
    public void Wait_events_map_to_the_monitor_categories(string evt, string? waitClass, string category)
    {
        Assert.Equal(category, OracleActivity.CategoryOf(evt, waitClass));
        if (waitClass == null) Assert.Equal(category, new OracleActivity().WaitCategory(evt));
    }

    [Theory]
    [InlineData("SQL*Net message from client", true)]
    [InlineData("PL/SQL lock timer", true)]
    [InlineData("rdbms ipc message", true)]
    [InlineData("Streams AQ: waiting for messages in the queue", true)]
    [InlineData("enq: TX - row lock contention", false)]
    [InlineData("db file sequential read", false)]
    public void Idle_class_waits_are_benign(string evt, bool benign) => Assert.Equal(benign, new OracleActivity().IsBenignWait(evt));

    [Fact]
    public void Identities_are_serial_and_logon_time_and_statement_starts_are_sql_id_and_execution()
    {
        var a = new OracleActivity();
        Assert.True(a.IsValidInstance("441:20261112061739"));
        Assert.False(a.IsValidInstance("") || a.IsValidInstance("441") || a.IsValidInstance("441:2026111206173") || a.IsValidInstance("x41:20261112061739")
            || a.IsValidInstance("441:2026111206173'") || a.IsValidInstance(":20261112061739"));
        Assert.True(a.IsValidStatementStart("f2tts8m7sb5cv:16777216"));
        Assert.False(a.IsValidStatementStart("F2TTS8M7SB5CV:16777216") || a.IsValidStatementStart("f2tts8m7sb5cv:") || a.IsValidStatementStart("f2tts8m7sb5c:16777216")
            || a.IsValidStatementStart("f2tts8m7sb5cv:1677'216") || a.IsValidStatementStart("f2tts8m7sb5cv:" + new string('9', 21)) || a.IsValidStatementStart(""));
    }

    [Theory]
    [InlineData("ACTIVE", false, "active")]
    [InlineData("ACTIVE", true, "active")]
    [InlineData("INACTIVE", true, "idle in transaction")]
    [InlineData("INACTIVE", false, "inactive")]
    [InlineData("KILLED", true, "killed")]
    [InlineData("SNIPED", false, "sniped")]
    public void Session_status_reads_in_the_monitors_words(string status, bool inTx, string expected) => Assert.Equal(expected, OracleActivity.StatusOf(status, inTx));

    [Fact]
    public void Lock_modes_and_commands_are_named()
    {
        Assert.Equal("row exclusive (SX)", OracleActivity.LockMode(3));
        Assert.Equal("exclusive (X)", OracleActivity.LockMode(6));
        Assert.Equal("7", OracleActivity.LockMode(7));
        Assert.Equal("SELECT", OracleActivity.CommandOf(3));
        Assert.Equal("PL/SQL EXECUTE", OracleActivity.CommandOf(47));
        Assert.Equal("MERGE", OracleActivity.CommandOf(189));
        Assert.Equal("command 250", OracleActivity.CommandOf(250));
        Assert.Null(OracleActivity.CommandOf(0));
    }

    [Fact]
    public void A_cancel_names_the_execution_the_dialog_showed_before_any_sql_is_built()
    {
        var a = new OracleActivity();
        MonitorKillParams P(string? start, KillMode mode = KillMode.CancelRequest, string instance = "441:20261112061739") =>
            new() { MonitorId = "m", Target = new SessionKey { Id = 66, Instance = instance }, Mode = mode, ExpectedStatementStart = start };
        Assert.Contains("Oracle", KillGuard.ValidateShape(P(null), a, Engine.Oracle));
        Assert.Equal("Invalid statement start.", KillGuard.ValidateShape(P("f2tts8m7sb5cv:1; DROP"), a, Engine.Oracle));
        Assert.Equal("Invalid session identity.", KillGuard.ValidateShape(P(null, KillMode.TerminateSession, "441:x"), a, Engine.Oracle));
        Assert.Null(KillGuard.ValidateShape(P("f2tts8m7sb5cv:16777216"), a, Engine.Oracle));
        Assert.Null(KillGuard.ValidateShape(P(null, KillMode.TerminateSession), a, Engine.Oracle));
    }
}
