using Querithm.Contract;
using Querithm.Core;
using Querithm.Providers.Oracle;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S12 feature matrix on the Oracle Free fixture (QUERITHM_IT=1): what Oracle lacks is said in session.capabilities with a
/// reason that names it, and the sidecar refuses a crafted call to it whatever the UI shows (§13 rule 7, release gate
/// "capability refusals at the RPC boundary").
/// </summary>
public sealed class OracleFeatureMatrixTests
{
    static async Task<(TestHost H, string Sid)> OpenAsync()
    {
        var h = new TestHost(new OracleProvider(Directory.CreateTempSubdirectory("qora-feat-").FullName));
        var p = await h.Rpc.ProfileSave(new Profile
        {
            Id = "", Name = "ora-features", Engine = Engine.Oracle, Host = "127.0.0.1", Port = OracleIntegrationTests.Port, Auth = AuthKind.Password,
            Username = OracleIntegrationTests.AppUser, DatabaseDefault = OracleIntegrationTests.Service, TlsMode = TlsMode.Disable, TimeoutS = 30,
        });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = FixtureSecrets.Password("oracle") });
        return (h, s.SessionId);
    }

    [IntegrationFact]
    public async Task Features_Oracle_lacks_say_why_and_the_sidecar_refuses_them()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var caps = await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid });
        foreach (var (name, info, word) in new[] { ("trace", caps.Trace, "Oracle"), ("debugger", caps.Debugger, "PL/SQL"), ("change database", caps.ChangeDatabase, "Oracle") })
        {
            Assert.False(info.Available, name);
            Assert.Contains(word, info.Reason);
        }
        // What S04–S12 implemented is offered.
        foreach (var (name, info) in new[] { ("monitor", caps.Monitor), ("data edit", caps.DataEdit), ("import", caps.Import), ("schema compare", caps.SchemaCompare),
                     ("data compare", caps.DataCompare), ("references", caps.References), ("transactions", caps.Transactions), ("estimated plan", caps.EstimatedPlan) })
            Assert.True(info.Available, name);

        // The UI hides these, but the sidecar is what refuses: a crafted call gets "unsupported" and nothing reaches the server.
        var trace = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.TraceStart(new TraceStartParams { SessionId = sid }));
        Assert.Equal("unsupported", trace.Code);
        var debug = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.DebugAttach(new DebugAttachParams { SessionId = sid, CallSql = "BEGIN NULL; END;" }));
        Assert.Equal("unsupported", debug.Code);
        var other = await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = "OTHERPDB" }));
        Assert.Contains("one service", other.Message);
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);

        // Diagnostics as PostgreSQL: no server-side parse diagnostics; errors come from the server with their position (S06).
        Assert.Empty(await h.Rpc.LangDiagnostics(new DiagnosticsParams { DocId = "d", Text = "SELEC 1 FROM dual", Dialect = Dialect.Oracle }));
        var (done, _, id) = await h.RunAsync(sid, "SELECT 1 FROM dual\nWHERE 1 = = 1");
        Assert.Equal(ExecState.Failed, done.State);
        Assert.Contains(h.Notifier.Of<ServerMessage>(), m => m.ExecutionId == id && m.EditorLine == 2);
    }
    /// <summary>D4 S12: the Oracle built-in snippets run as Monaco inserts them (placeholders pointed at a fixture table).</summary>
    [IntegrationFact]
    public async Task The_oracle_snippets_run_as_inserted()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        await h.RunAsync(sid, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE q_s12_snip PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");
        Assert.Equal(ExecState.Done, (await h.RunAsync(sid, "CREATE TABLE q_s12_snip (id NUMBER PRIMARY KEY, col VARCHAR2(10))")).Done.State);
        var values = new Dictionary<string, string> { ["table_name"] = "q_s12_snip", ["column_name"] = "col", ["pkg_name"] = "q_s12_snip_pkg" };
        try
        {
            foreach (var snippet in Querithm.Core.Store.BuiltInSnippets.All.Where(b => b.Dialect == Dialect.Oracle))
            {
                var (done, _, id) = await h.RunAsync(sid, SnippetTests.Expanded(snippet.Body, values));
                Assert.True(done.State == ExecState.Done, $"{snippet.Prefix}: {done.Error?.Message} {string.Join(" | ", h.Messages(id))}");
            }
            var (rows, batches, _) = await h.RunAsync(sid, "SELECT COUNT(*) AS n FROM q_s12_snip");
            Assert.Equal(ExecState.Done, rows.State);
            Assert.Single(batches); // the MERGE inserted its row
            Assert.Equal(ExecState.Done, (await h.RunAsync(sid, "BEGIN q_s12_snip_pkg.do_work(1); END;")).Done.State);
        }
        finally
        {
            await h.RunAsync(sid, "BEGIN EXECUTE IMMEDIATE 'DROP PACKAGE q_s12_snip_pkg'; EXCEPTION WHEN OTHERS THEN NULL; END;");
            await h.RunAsync(sid, "DROP TABLE q_s12_snip PURGE");
        }
    }
}
