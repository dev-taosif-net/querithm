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
/// D4 S01: Oracle is a hidden engine skeleton. Every feature refuses with a reason, no SQL is read (every statement is a
/// hard refusal in the sidecar, whatever the UI sends), nothing connects, and secrets cannot leak from text it cannot read.
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
    public void Every_feature_refuses_with_a_reason(Feature feature)
    {
        var reason = _oracle.FeatureRefusal(feature);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("Oracle", reason);
        Assert.False(FeatureGuard.Info(_oracle, feature).Available);
    }

    [Theory]
    [InlineData("SELECT 1 FROM DUAL")]
    [InlineData("DROP TABLE t PURGE")]
    [InlineData("BEGIN\n  DELETE FROM t;\nEND;\n/")]
    [InlineData("")]
    public void Every_statement_is_refused_before_anything_runs(string sql)
    {
        var c = _oracle.Language.Classify(sql);
        Assert.Equal(OracleLanguageService.NotReadYet, c.Refusal);
        var batch = Assert.Single(c.Batches);
        Assert.Equal((0, sql.Length, 1, sql.Split('\n').Length), (batch.StartOffset, batch.Length, batch.StartLine, batch.EndLine));
    }

    [Fact]
    public void Secrets_cannot_leak_from_text_that_is_not_read_yet()
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
        Assert.Equal(("select 1 from dual", OracleLanguageService.NotReadYet), (f.Text, f.Error));
    }

    [Fact]
    public async Task Nothing_connects_yet_and_each_refusal_names_the_reason()
    {
        var p = new Profile { Id = "x", Name = "ora", Engine = Engine.Oracle, Host = "db.invalid", Port = 1521, Auth = AuthKind.Password, Username = "u" };
        Assert.Null(_oracle.ValidateProfile(p));
        Assert.NotNull(_oracle.ValidateProfile(p with { Location = LocationKind.File }));
        Assert.NotNull(_oracle.ValidateProfile(p with { Auth = AuthKind.Windows }));
        Assert.Equal(OracleProvider.NotConnectingYet, (await Assert.ThrowsAsync<NotSupportedException>(() => _oracle.OpenAsync(p, "pw", null, null, CancellationToken.None))).Message);
        Assert.Equal(OracleProvider.NotConnectingYet, Assert.Throws<NotSupportedException>(() => _oracle.BuildConnectionString(p, "pw", null)).Message);
        Assert.Equal(OracleProvider.NotConnectingYet, _oracle.TunnelTarget(p).Refusal);
        Assert.Throws<ArgumentException>(() => _oracle.ParseConnectionString("Data Source=//h:1521/s", p));
        Assert.NotNull(_oracle.Plans.Refusal("SELECT 1 FROM DUAL", PlanMode.Estimated));
        Assert.Equal((1521, AuthKind.Password, "Oracle"), (_oracle.Traits.DefaultPort, _oracle.Traits.DefaultAuth, _oracle.Traits.Name));
        Assert.Equal("Oracle", ProfileRules.EngineName(Engine.Oracle));
    }

    /// <summary>Exposed through the dev setting: a profile saves and a tab opens (it dials nothing), but a run is refused with the reason.</summary>
    [Fact]
    public async Task Exposed_by_the_dev_setting_a_run_is_refused_with_the_reason()
    {
        var dir = Directory.CreateTempSubdirectory("qora-").FullName;
        var registry = new ProviderRegistry(EngineSetup.Providers(dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle"));
        await using (var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance, registry, dir))
        {
            var rpc = new RpcServer(services, () => { });
            Assert.True((await rpc.Engines()).Single(e => e.Engine == Engine.Oracle).Exposed);
            var saved = await rpc.ProfileSave(new Profile { Id = "", Name = "ora", Engine = Engine.Oracle, Host = "db.invalid", Port = 1521, Auth = AuthKind.Password, Username = "u" });
            var session = await rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = "pw" });
            var refused = await Assert.ThrowsAsync<QuerithmException>(() => rpc.QueryExecute(new QueryExecuteParams { SessionId = session.SessionId, Sql = "SELECT 1 FROM DUAL" }));
            Assert.Equal(("refused", OracleLanguageService.NotReadYet), (refused.Code, refused.Message));
        }
        Directory.Delete(dir, true);
    }

    sealed class NoSchema : ISchemaSource
    {
        public SchemaSnapshot? Get(string? profileId, string? database) => null;
        public IReadOnlyList<string> Databases(string? profileId) => [];
    }
}
