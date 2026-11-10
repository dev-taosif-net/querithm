using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Intelligence.Oracle;
using Querithm.Providers.Oracle;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// D4 S06, the sidecar side of the webview work: the Intel macOS refusal (O6), editor columns for ORA- positions, and the
/// statement / bind parity the webview's own Oracle splitter is held to (one fixture file both test suites read).
/// </summary>
public class OracleEditorSupportTests
{
    // ---- O6: Intel macOS ----

    [Theory]
    [InlineData(true, Architecture.X64, true)]
    [InlineData(true, Architecture.Arm64, false)]
    [InlineData(false, Architecture.X64, false)]
    [InlineData(false, Architecture.Arm64, false)]
    public void Only_an_Intel_Mac_refuses_Oracle(bool macOs, Architecture arch, bool refused)
    {
        var reason = OracleProvider.PlatformRefusal(macOs, arch);
        Assert.Equal(refused, reason != null);
        if (reason != null) Assert.Contains("Intel Mac", reason);
    }

    [Fact]
    public void This_computer_is_not_refused_unless_it_is_an_Intel_Mac() =>
        Assert.Equal(OracleProvider.PlatformRefusal(OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture), new OracleProvider().Traits.Unavailable);

    const string IntelMac = "Oracle's driver (ODP.NET) does not run on Intel Macs. Oracle connections work in Querithm on Apple-silicon Macs, Windows and Linux.";

    static (Services Services, RpcServer Rpc, string Dir) HostWithUnavailableOracle()
    {
        var dir = Directory.CreateTempSubdirectory("qora-mac-").FullName;
        var registry = new ProviderRegistry([new SqlServerProvider(), new OracleProvider(dir, IntelMac)]);
        var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance, registry, dir);
        return (services, new RpcServer(services, () => { }), dir);
    }

    static Profile OracleProfile() => new()
    {
        Id = "", Name = "ora", Engine = Engine.Oracle, Host = "db.example", Port = 2484, Auth = AuthKind.Password, Username = "app", DatabaseDefault = "FREEPDB1",
    };

    [Fact]
    public async Task On_an_Intel_Mac_Oracle_is_listed_with_the_reason_and_no_profile_saves_tests_or_opens()
    {
        var (services, rpc, dir) = HostWithUnavailableOracle();
        await using var _ = services;
        try
        {
            var info = (await rpc.Engines()).Single(e => e.Engine == Engine.Oracle);
            Assert.Equal(IntelMac, info.Unavailable);
            Assert.Null((await rpc.Engines()).Single(e => e.Engine == Engine.Mssql).Unavailable);
            var save = await Assert.ThrowsAsync<QuerithmException>(() => rpc.ProfileSave(OracleProfile()));
            Assert.Contains("Intel Macs", save.Message);
            var test = await rpc.ProfileTest(new ProfileTestParams { Profile = OracleProfile(), Secret = "x" });
            Assert.False(test.Ok);
            Assert.Contains("Intel Macs", test.Error);
            // A profile stored on another computer (a copied store) still refuses to open, with the same reason.
            var stored = services.Profiles.Save(OracleProfile());
            var open = Assert.Throws<QuerithmException>(() => services.Sessions.Open(new SessionOpenParams { ProfileId = stored.Id }));
            Assert.Contains("Intel Macs", open.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- editor columns ----

    static BatchInfo Batch(int startOffset) => new() { Index = 0, StartLine = 1, EndLine = 1, StartOffset = startOffset, Length = 1 };

    [Theory]
    [InlineData("SELECT nope FROM dual", 0, 1, 8, 8)]
    [InlineData("SELECT 1 FROM dual; SELECT nope FROM dual", 20, 1, 8, 28)]
    [InlineData("SELECT 1 FROM dual;\n  SELECT nope FROM dual", 22, 1, 8, 10)]
    [InlineData("SELECT 1 FROM dual; BEGIN\n  x := 1;\nEND;", 20, 2, 3, 3)] // later lines are not shifted
    public void A_column_on_the_batchs_first_line_shifts_by_where_the_batch_starts(string sql, int start, int line, int column, int editor) =>
        Assert.Equal(editor, ExecutionService.EditorColumn(sql, Batch(start), line, column));

    [Fact]
    public void No_column_or_line_means_no_editor_column()
    {
        Assert.Null(ExecutionService.EditorColumn("SELECT 1", Batch(0), 1, null));
        Assert.Null(ExecutionService.EditorColumn("SELECT 1", Batch(0), 1, 0));
        Assert.Null(ExecutionService.EditorColumn("SELECT 1", Batch(0), null, 3));
        Assert.Equal(3, ExecutionService.EditorColumn("SELECT 1", null, 1, 3));
        // An offset past the text (a stale batch) cannot move the column outside it.
        Assert.Equal(12, ExecutionService.EditorColumn("SELECT 1", Batch(99), 1, 4));
    }

    // ---- statement and bind parity with the webview (src/editor/completion/oracleStatements.ts) ----

    sealed record Expected(string Kind, int Start, int End);
    sealed record Case(string Name, string Text, string[]? Binds, Expected[]? Statements);

    static string FixturePath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "package.json")) && Directory.Exists(Path.Combine(d.FullName, "src", "editor")))
                return Path.Combine(d.FullName, "src", "editor", "completion", "__tests__", "fixtures", "oracle-statements.json");
        throw new DirectoryNotFoundException("repository root");
    }

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in JsonSerializer.Deserialize<Case[]>(File.ReadAllText(FixturePath()), Web)!) data.Add(c.Name);
        return data;
    }

    static string KindText(OracleStatementKind k) => k switch
    {
        OracleStatementKind.Sql => "sql",
        OracleStatementKind.PlSql => "plsql",
        OracleStatementKind.ClientCommand => "client",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_splitter_and_bind_scan_answer_what_the_shared_fixture_says(string name)
    {
        var c = JsonSerializer.Deserialize<Case[]>(File.ReadAllText(FixturePath()), Web)!.Single(x => x.Name == name);
        var statements = OracleSplitter.Split(c.Text).Select(s => new Expected(KindText(s.Kind), s.Start, s.End)).ToArray();
        var binds = OracleLanguageService.Binds(c.Text).ToArray();
        var actual = JsonSerializer.Serialize(new { binds, statements }, Web);
        Assert.True(c.Binds != null && c.Statements != null, $"fixture case '{name}' has no expectation; the sidecar says {actual}");
        Assert.Equal(JsonSerializer.Serialize(new { binds = c.Binds, statements = c.Statements }, Web), actual);
    }
}
