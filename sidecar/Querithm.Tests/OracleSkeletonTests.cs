using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Intelligence.Oracle;
using Querithm.Providers.Oracle;

namespace Querithm.Tests;

/// <summary>
/// D4 S01: Oracle is a hidden engine. Every feature not implemented yet refuses with a reason (transactions since D4 S03).
/// Since D4 S02 the SQL is read (OracleIntelligenceTests); since D4 S03 a profile connects (OracleProviderTests and
/// OracleProviderIntegrationTests).
/// </summary>
public class OracleSkeletonTests
{
    readonly OracleProvider _oracle = new();

    public static TheoryData<Feature> Features
    {
        get
        {
            var data = new TheoryData<Feature>();
            foreach (var f in Enum.GetValues<Feature>()) data.Add(f);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Features))]
    public void Every_feature_not_implemented_yet_refuses_with_a_reason(Feature feature)
    {
        if (feature == Feature.Transactions)
        {
            Assert.Null(_oracle.FeatureRefusal(feature));
            return;
        }
        var reason = _oracle.FeatureRefusal(feature);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("Oracle", reason);
        Assert.False(FeatureGuard.Info(_oracle, feature).Available);
    }

    [Fact]
    public void A_quoted_password_does_not_leak()
    {
        var r = _oracle.Language.RedactSecrets("CREATE USER app IDENTIFIED BY \"s3cret-Value\"");
        Assert.DoesNotContain("s3cret", r.Text);
        Assert.Equal("", _oracle.Language.RedactSecrets("").Text);
    }

    [Fact]
    public void Editor_services_answer_empty_and_formatting_says_why()
    {
        Assert.Empty(_oracle.Language.Complete(new CompleteParams { DocId = "d", Text = "SEL", Offset = 3, Dialect = Dialect.Oracle }, new NoSchema()));
        Assert.Empty(_oracle.Language.Diagnostics(new DiagnosticsParams { DocId = "d", Text = "SELECT", Dialect = Dialect.Oracle }));
        var f = _oracle.Language.Format(new FormatParams { Text = "select 1 from dual", Dialect = Dialect.Oracle });
        Assert.Equal(("select 1 from dual", OracleLanguageService.NoFormatter), (f.Text, f.Error));
    }

    [Fact]
    public void Server_profiles_are_checked_and_the_tunnel_refuses_until_d4_s07()
    {
        var p = new Profile { Id = "x", Name = "ora", Engine = Engine.Oracle, Host = "db.invalid", Port = 1521, Auth = AuthKind.Password, Username = "u", DatabaseDefault = "S" };
        Assert.Null(_oracle.ValidateProfile(p));
        Assert.NotNull(_oracle.ValidateProfile(p with { Location = LocationKind.File }));
        Assert.NotNull(_oracle.ValidateProfile(p with { Auth = AuthKind.Windows }));
        Assert.Equal(OracleProvider.NoTunnelYet, _oracle.TunnelTarget(p).Refusal);
        Assert.NotNull(_oracle.Plans.Refusal("SELECT 1 FROM DUAL", PlanMode.Estimated));
        Assert.Equal((2484, AuthKind.Password, "Oracle"), (_oracle.Traits.DefaultPort, _oracle.Traits.DefaultAuth, _oracle.Traits.Name));
        Assert.Equal("Oracle", ProfileRules.EngineName(Engine.Oracle));
    }

    /// <summary>Exposed through the dev setting: a profile saves and a tab opens (it dials nothing); a run passes the guard, a SQL*Plus command is refused.</summary>
    [Fact]
    public async Task Exposed_by_the_dev_setting_a_run_passes_the_guard_and_client_commands_are_refused()
    {
        var dir = Directory.CreateTempSubdirectory("qora-").FullName;
        var registry = new ProviderRegistry(EngineSetup.Providers(dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle"));
        await using (var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance, registry, dir))
        {
            var rpc = new RpcServer(services, () => { });
            Assert.True((await rpc.Engines()).Single(e => e.Engine == Engine.Oracle).Exposed);
            var saved = await rpc.ProfileSave(new Profile { Id = "", Name = "ora", Engine = Engine.Oracle, Host = "db.invalid", Port = 1521, Auth = AuthKind.Password, Username = "u" });
            var session = await rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = "pw" });
            var run = await rpc.QueryExecute(new QueryExecuteParams { SessionId = session.SessionId, Sql = "SELECT 1 FROM DUAL" });
            Assert.Null(run.Blocked);
            Assert.NotNull(run.ExecutionId);
            var refused = await Assert.ThrowsAsync<QuerithmException>(() => rpc.QueryExecute(new QueryExecuteParams { SessionId = session.SessionId, Sql = "SPOOL x.txt" }));
            Assert.Equal("refused", refused.Code);
            Assert.Contains("SPOOL", refused.Message);
        }
        Directory.Delete(dir, true);
    }

    sealed class NoSchema : ISchemaSource
    {
        public SchemaSnapshot? Get(string? profileId, string? database) => null;
        public IReadOnlyList<string> Databases(string? profileId) => [];
    }
}
