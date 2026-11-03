using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Store;
using Querithm.Host;

namespace Querithm.Tests;

/// <summary>The JSON-RPC surface end to end for the language, settings and metadata methods, for both dialects.</summary>
public class RpcSurfaceTests
{
    [Theory]
    [InlineData(Dialect.Tsql, "SELECT a ,b  FROM  dbo.t WHERE a=1", "FROM")]
    [InlineData(Dialect.Pg, "select a ,b  from  public.t where a=1", "from")]
    public async Task Language_methods_answer_for_each_dialect(Dialect dialect, string sql, string fromWord)
    {
        await using var h = new TestHost();
        var format = await h.Rpc.LangFormat(new FormatParams { Text = sql, Dialect = dialect });
        if (dialect == Dialect.Tsql)
        {
            Assert.Null(format.Error);
            Assert.Contains(fromWord, format.Text, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(sql, format.Text);
        }
        else
        {
            // PostgreSQL has no server-side formatter: it degrades with a visible reason and returns the text untouched.
            Assert.Equal("client", format.Error);
            Assert.Equal(sql, format.Text);
        }

        var diagnostics = await h.Rpc.LangDiagnostics(new DiagnosticsParams { DocId = "d-" + dialect, Text = "SELECT FROM WHERE (((", Dialect = dialect });
        if (dialect == Dialect.Tsql) Assert.NotEmpty(diagnostics);   // PostgreSQL syntax errors come from the server on execution
        Assert.All(diagnostics, d => Assert.False(string.IsNullOrWhiteSpace(d.Message)));

        var multi = dialect == Dialect.Tsql ? "SELECT 1;\nGO\nDROP TABLE dbo.t;\nBEGIN TRANSACTION" : "SELECT 1;\nDROP TABLE t;\nBEGIN";
        var classified = await h.Rpc.LangClassify(new ClassifyParams { Text = multi, Dialect = dialect });
        Assert.True(classified.HasDdl);
        Assert.True(classified.BeginsTransaction);
        Assert.NotEmpty(classified.Destructive);
        Assert.NotEmpty(classified.Batches);

        var folding = await h.Rpc.LangFolding(new FoldingParams { DocId = "f-" + dialect, Text = "BEGIN\n  SELECT 1;\n  SELECT 2;\nEND\n-- c\n", Dialect = dialect });
        Assert.NotNull(folding);

        var text = dialect == Dialect.Tsql ? "SELECT ISNULL(a, " : "SELECT coalesce(a, ";
        var sig = await h.Rpc.LangSignature(new SignatureParams { DocId = "s-" + dialect, Text = text, Offset = text.Length, Dialect = dialect });
        Assert.NotNull(sig);

        var complete = await h.Rpc.LangComplete(new CompleteParams { DocId = "c-" + dialect, Text = "SEL", Offset = 3, Dialect = dialect });
        Assert.NotNull(complete);
    }

    [Fact]
    public async Task Settings_round_trip_and_unknown_keys_are_simply_absent()
    {
        await using var h = new TestHost();
        await h.Rpc.SettingsSet(new SettingEntry { Key = "ui.theme", ValueJson = "\"dark\"" });
        await h.Rpc.SettingsSet(new SettingEntry { Key = "editor.size", ValueJson = "14" });
        var some = await h.Rpc.SettingsGet(new SettingsGetParams { Keys = ["ui.theme", "nope"] });
        var theme = Assert.Single(some);
        Assert.Equal("\"dark\"", theme.ValueJson);
        Assert.Equal(2, (await h.Rpc.SettingsGet(new SettingsGetParams())).Length);
        await h.Rpc.SettingsSet(new SettingEntry { Key = "ui.theme", ValueJson = "\"light\"" });
        Assert.Equal("\"light\"", Assert.Single(await h.Rpc.SettingsGet(new SettingsGetParams { Keys = ["ui.theme"] })).ValueJson);
    }

    [Fact]
    public async Task Metadata_queries_read_the_local_cache_for_a_profile()
    {
        await using var h = new TestHost();
        var profile = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "cache", Engine = Engine.Pg, Host = "h" });
        h.Services.Metadata.Store.ApplyRefresh(profile.Id, "db", true,
            [new(1, "public", "orders", ObjectKind.Table, 1, null), new(2, "public", "order_lines", ObjectKind.Table, 1, null)], [],
            new(Array.Empty<Querithm.Contract.Providers.ColumnRow>(), [], [], []), "16");
        var found = await h.Rpc.MetaSearch(new SearchParams { ProfileId = profile.Id, Database = "db", Text = "order" });
        Assert.Equal(2, found.Length);
        Assert.Single(await h.Rpc.MetaSearch(new SearchParams { ProfileId = profile.Id, Database = "db", Text = "order", Limit = 1 }));
        var objects = await h.Rpc.MetaObjects(new ObjectsParams { ProfileId = profile.Id, Database = "db", Ids = [2] });
        Assert.Equal("order_lines", Assert.Single(objects).Name);
        var details = await h.Rpc.MetaObjectDetails(new ObjectDetailsParams { ProfileId = profile.Id, Database = "db", ObjectId = 1 });
        Assert.Empty(details.Columns);
        var snapshot = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = profile.Id, Database = "db" });
        Assert.Equal(2, snapshot.Objects.Length);
        Assert.Equal(SnapshotStatus.Fresh, snapshot.Status);
    }

    [Fact]
    public async Task Shutdown_asks_the_host_to_exit_shortly_after_answering()
    {
        var asked = new TaskCompletionSource();
        await using var h = new TestHost();
        var server = new RpcServer(h.Services, () => asked.TrySetResult());
        await server.Shutdown();
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ping = await server.Ping();
        Assert.Equal(Environment.ProcessId, ping.Pid);
        Assert.True(ping.WorkingSetBytes > 0);
        Assert.False(string.IsNullOrEmpty(ping.Version));
    }

    [Fact]
    public async Task Profiles_need_a_name_and_missing_profiles_cannot_be_tested()
    {
        await using var h = new TestHost();
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.ProfileSave(new Profile { Id = "", Name = "  ", Engine = Engine.Mssql, Host = "h" }));
        Assert.Equal("invalid", ex.Code);
        await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.ProfileTest(new ProfileTestParams { ProfileId = "no-such-profile" }));
    }

    [Fact]
    public async Task A_connection_failure_without_a_message_still_tells_the_user_something()
    {
        // Regression: the trimmed Windows sidecar failed TLS pre-login with an empty TypeLoadException and the editor showed a blank error.
        var fake = new MetadataRefreshTests.FakeProvider(new MetadataRefreshTests.SlowMetadata(), new TypeLoadExceptionWithoutMessage());
        var dir = Directory.CreateTempSubdirectory("qhost-").FullName;
        await using var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, new Core.Sessions.ProviderRegistry([fake]), dir);
        var rpc = new RpcServer(services, () => { });
        var r = await rpc.ProfileTest(new ProfileTestParams { Profile = new Profile { Id = "", Name = "fake", Engine = Engine.Pg, Host = "h" }, Secret = "x" });
        Assert.False(r.Ok);
        Assert.Contains(nameof(TypeLoadExceptionWithoutMessage), r.Error);
    }

    sealed class TypeLoadExceptionWithoutMessage() : TypeLoadException("");
}
