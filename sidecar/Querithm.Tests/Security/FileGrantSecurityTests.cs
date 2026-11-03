using System.Diagnostics;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Logging;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Tests.Fakes;

namespace Querithm.Tests.Security;

/// <summary>
/// Phase D0c (§13 rule 6, amended): the sidecar opens a database file only through the grant the shell injected for that
/// profile, in that grant's mode, after checking the file is the picked one — before and after the driver opens it. The
/// shell's half (registry, binding, forged ids, consent) is tested in src-tauri (grants.rs, injection.rs).
/// </summary>
public sealed class FileGrantSecurityTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qgrant-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    string NewFile(string name = "sales.db")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, "SQLite format 3\0"u8.ToArray());
        return path;
    }

    static FileGrant GrantFor(string path, string id = "g1", FileAccessMode mode = FileAccessMode.ReadOnly) =>
        new() { GrantId = id, Path = path, Mode = mode, Identity = FileIdentity.Of(path)! };

    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public ProfileStore Profiles { get; } = new(LocalStore.InMemory());
        public SessionManager Sessions { get; }
        public CapturingNotifier Notifier { get; } = new();

        public Rig() => Sessions = new SessionManager(Profiles, new ProviderRegistry([Provider]), Notifier, Log.Null);

        public Profile FileProfile(string grantId = "g1") =>
            Profiles.Save(new Profile { Id = "", Name = "file", Engine = Engine.Mssql, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = grantId, DisplayName = "sales.db" } });

        public ValueTask DisposeAsync() => Sessions.DisposeAsync();
    }

    // ---------------------------------------------------------------------------------------------- identity

    [Fact]
    public void The_identity_matches_the_shells_format_and_follows_the_file_not_the_path()
    {
        var a = NewFile("a.db");
        var id = FileIdentity.Of(a)!;
        Assert.Matches(OperatingSystem.IsWindows() ? "^w:\\d+:\\d+$" : "^u:\\d+:\\d+$", id);
        File.AppendAllText(a, "more"); // a write keeps the identity
        Assert.Equal(id, FileIdentity.Of(a));
        var b = NewFile("b.db");
        Assert.NotEqual(id, FileIdentity.Of(b));
        File.Move(b, a, overwrite: true); // the path now names another file
        Assert.NotEqual(id, FileIdentity.Of(a));
        Assert.Null(FileIdentity.Of(Path.Combine(_dir, "missing.db")));
        Assert.Null(FileIdentity.Of(_dir));
    }

    [Fact]
    public void The_identity_is_read_while_an_engine_holds_the_file_open_exclusively()
    {
        // Regression: Windows read the identity through a handle asking for read access, which an engine's exclusive open
        // (DuckDB opens a writable database with no sharing) refuses; the check after the open then said the file was gone.
        var a = NewFile();
        var id = FileIdentity.Of(a)!;
        using (new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(id, FileIdentity.Of(a));
    }

    [Fact]
    public void The_identity_is_read_on_a_path_longer_than_260_characters()
    {
        var deep = Path.Combine(_dir, new string('d', 120), new string('e', 120));
        Directory.CreateDirectory(deep);
        var a = Path.Combine(deep, "sales.db");
        File.WriteAllBytes(a, "SQLite format 3\0"u8.ToArray());
        Assert.True(a.Length > 260);
        Assert.Matches(OperatingSystem.IsWindows() ? "^w:\\d+:\\d+$" : "^u:\\d+:\\d+$", FileIdentity.Of(a));
    }

    [Fact]
    public void The_identity_agrees_with_stat()
    {
        if (!OperatingSystem.IsLinux()) return;
        var a = NewFile();
        var psi = new ProcessStartInfo("stat", ["-c", "%d:%i", a]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var expected = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.Equal($"u:{expected}", FileIdentity.Of(a));
    }

    // ---------------------------------------------------------------------------------------------- open

    [Fact]
    public async Task A_file_profile_without_its_injected_grant_is_never_opened()
    {
        await using var rig = new Rig();
        var p = rig.FileProfile();
        var ex = Assert.Throws<QuerithmException>(() => rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id }));
        Assert.Equal("fileGrant", ex.Code);
        Assert.Empty(rig.Provider.Opened);
        Assert.False(rig.Sessions.CanOpen(p));
    }

    [Fact]
    public async Task Another_profiles_grant_is_refused()
    {
        await using var rig = new Rig();
        var p = rig.FileProfile("mine");
        var ex = Assert.Throws<QuerithmException>(() => rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = GrantFor(NewFile(), "someone-else") }));
        Assert.Equal("fileGrant", ex.Code);
    }

    [Theory]
    [InlineData("relative/sales.db")]
    [InlineData("dotdot")]
    [InlineData("nul")]
    public async Task A_path_that_is_not_the_shells_canonical_form_is_refused(string kind)
    {
        await using var rig = new Rig();
        var p = rig.FileProfile();
        var real = NewFile();
        var path = kind switch
        {
            "dotdot" => Path.Combine(_dir, "sub", "..", "sales.db"),
            "nul" => real + "\0.txt",
            _ => kind,
        };
        var grant = new FileGrant { GrantId = "g1", Path = path, Mode = FileAccessMode.ReadOnly, Identity = FileIdentity.Of(real)! };
        Assert.Equal("fileGrant", Assert.Throws<QuerithmException>(() => rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = grant })).Code);
    }

    [Fact]
    public async Task The_granted_file_is_opened_in_the_grants_mode_and_never_escalated()
    {
        await using var rig = new Rig();
        var p = rig.FileProfile();
        var path = NewFile();
        var s = rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = GrantFor(path) });
        await rig.Sessions.StateAsync(s.SessionId, default);
        var given = Assert.Single(rig.Provider.OpenedFiles);
        Assert.Equal((path, FileAccessMode.ReadOnly), (given!.Path, given.Mode));
        Assert.True(rig.Sessions.CanOpen(p)); // background work may open it now, with this grant
    }

    [Fact]
    public async Task A_replaced_file_is_never_opened()
    {
        await using var rig = new Rig();
        var p = rig.FileProfile();
        var path = NewFile();
        var grant = GrantFor(path);
        var s = rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = grant });
        File.Move(NewFile("evil.db"), path, overwrite: true);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => rig.Sessions.StateAsync(s.SessionId, default));
        Assert.Equal("fileReplaced", ex.Code);
        Assert.Empty(rig.Provider.Opened);
    }

    [Fact]
    public async Task A_file_swapped_while_the_driver_opened_it_is_closed_again()
    {
        await using var rig = new Rig();
        var p = rig.FileProfile();
        var path = NewFile();
        var s = rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = GrantFor(path) });
        // The check before the open passes; the file is replaced before the check after it.
        rig.Provider.OnOpen = _ => { File.Move(NewFile("swap.db"), path, overwrite: true); return Task.CompletedTask; };
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => rig.Sessions.StateAsync(s.SessionId, default));
        Assert.Equal("fileReplaced", ex.Code);
        var opened = Assert.Single(rig.Provider.Opened);
        Assert.Equal(1, opened.DisposeCount);
    }

    [Fact]
    public async Task A_missing_file_is_reported_and_never_created()
    {
        await using var rig = new Rig();
        var p = rig.FileProfile();
        var path = NewFile();
        var s = rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = GrantFor(path) });
        File.Delete(path);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => rig.Sessions.StateAsync(s.SessionId, default));
        Assert.Equal("fileMissing", ex.Code);
        Assert.Contains("Re-pick file", ex.Message);
        Assert.False(File.Exists(path));
        Assert.Empty(rig.Provider.Opened);
    }

    [Fact]
    public async Task A_symlink_planted_on_the_path_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var rig = new Rig();
        var p = rig.FileProfile();
        var path = NewFile();
        var grant = GrantFor(path);
        var s = rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = grant });
        // Same inode behind a link: the identity alone would pass, the link does not.
        var moved = Path.Combine(_dir, "elsewhere.db");
        File.Move(path, moved);
        File.CreateSymbolicLink(path, moved);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => rig.Sessions.StateAsync(s.SessionId, default));
        Assert.Equal("fileMoved", ex.Code);
    }

    [Fact]
    public async Task A_changed_grant_closes_connections_built_under_the_old_one()
    {
        await using var rig = new Rig();
        var p = rig.FileProfile();
        var path = NewFile();
        rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = GrantFor(path) });
        var epoch = rig.Sessions.CredentialEpoch(p.Id);
        rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = GrantFor(path) });
        Assert.Equal(epoch, rig.Sessions.CredentialEpoch(p.Id)); // the same grant again: nothing to rebuild
        rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = GrantFor(path, mode: FileAccessMode.ReadWrite) });
        Assert.True(rig.Sessions.CredentialEpoch(p.Id) > epoch);
        Assert.Equal(FileAccessMode.ReadWrite, rig.Sessions.FileGrantFor(p.Id)!.Mode);
    }

    [Fact]
    public async Task After_a_sidecar_restart_nothing_is_opened_until_the_shell_injects_the_grant_again()
    {
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var path = NewFile();
        var p = profiles.Save(new Profile { Id = "", Name = "file", Engine = Engine.Mssql, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = "g1" } });
        await using (var first = new SessionManager(profiles, new ProviderRegistry([new FakeDbProvider()]), new CapturingNotifier(), Log.Null))
        {
            first.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = GrantFor(path) });
            Assert.True(first.CanOpen(p));
        }
        await using var restarted = new SessionManager(profiles, new ProviderRegistry([new FakeDbProvider()]), new CapturingNotifier(), Log.Null);
        Assert.False(restarted.CanOpen(p));
        Assert.Null(restarted.FileGrantFor(p.Id));
    }

    [Fact]
    public async Task Disconnecting_forgets_the_grant_like_a_secret()
    {
        await using var rig = new Rig();
        var p = rig.FileProfile();
        rig.Sessions.Open(new SessionOpenParams { ProfileId = p.Id, FileGrant = GrantFor(NewFile()) });
        rig.Sessions.ForgetProfile(p.Id);
        Assert.False(rig.Sessions.CanOpen(p));
    }

    // ---------------------------------------------------------------------------------------------- revocation (RPC)

    static async Task<(TestHostWithFake Host, Profile Profile, string SessionId)> Revocable(string path)
    {
        var host = new TestHostWithFake();
        var p = host.Services.Profiles.Save(new Profile
        {
            Id = "", Name = "file", Engine = Engine.Mssql, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = "g1" },
        });
        var s = await host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, FileGrant = GrantFor(path) });
        await host.Rpc.SessionStateGet(new SessionRef { SessionId = s.SessionId });
        return (host, p, s.SessionId);
    }

    [Fact]
    public async Task Revoking_is_refused_while_a_transaction_is_open_and_keeps_the_tab()
    {
        var (host, p, sid) = await Revocable(NewFile());
        await using var _ = host;
        await host.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.GrantRevoke(new GrantRevokeParams { ProfileId = p.Id }));
        Assert.Equal("grantBusy", ex.Code);
        Assert.Equal(TxState.Open, (await host.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.NotNull(host.Services.Sessions.FileGrantFor(p.Id));
    }

    [Fact]
    public async Task Revoking_is_refused_while_a_query_runs()
    {
        var (host, p, sid) = await Revocable(NewFile());
        await using var _ = host;
        var gate = new TaskCompletionSource();
        host.Provider.Handler = async (cmd, ct) => { if (cmd.CommandText.StartsWith("WAITFOR", StringComparison.Ordinal)) await gate.Task.WaitAsync(ct); return FakeResult.None; };
        var exec = await host.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "WAITFOR DELAY '00:01'" });
        while (host.Services.Sessions.Get(sid).ActiveExecutionId == null) await Task.Delay(5);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.GrantRevoke(new GrantRevokeParams { ProfileId = p.Id }));
        Assert.Equal("grantBusy", ex.Code);
        gate.SetResult();
        await host.Notifier.WaitDone(exec.ExecutionId!);
    }

    [Fact]
    public async Task Revoking_an_idle_file_closes_its_sessions_and_forgets_the_grant()
    {
        var (host, p, sid) = await Revocable(NewFile());
        await using var _ = host;
        var r = await host.Rpc.GrantRevoke(new GrantRevokeParams { ProfileId = p.Id });
        Assert.Equal(1, r.ClosedSessions);
        Assert.Throws<QuerithmException>(() => host.Services.Sessions.Get(sid));
        Assert.Null(host.Services.Sessions.FileGrantFor(p.Id));
        Assert.False(host.Services.Sessions.CanOpen(p));
        Assert.Equal("fileGrant", Assert.Throws<QuerithmException>(() => host.Services.Sessions.Open(new SessionOpenParams { ProfileId = p.Id })).Code);
    }

    [Fact]
    public async Task Profile_test_opens_only_an_injected_grant()
    {
        await using var host = new TestHostWithFake();
        var path = NewFile();
        var unsaved = new Profile { Id = "", Name = "new", Engine = Engine.Mssql, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = "g1" } };
        var refused = await host.Rpc.ProfileTest(new ProfileTestParams { Profile = unsaved });
        Assert.False(refused.Ok);
        Assert.Empty(host.Provider.Opened);
        var ok = await host.Rpc.ProfileTest(new ProfileTestParams { Profile = unsaved, FileGrant = GrantFor(path) });
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal(path, Assert.Single(host.Provider.OpenedFiles)!.Path);
    }

    // ---------------------------------------------------------------------------------------------- paths are user data

    [Fact]
    public void Granted_paths_and_their_folder_are_scrubbed_from_logs_and_errors()
    {
        var path = Path.Combine(_dir, "customers", "ledger.db");
        LogScrubber.RegisterPath(path);
        var scrubbed = LogScrubber.Scrub($"SQLite Error 14: unable to open database file '{path}' (journal in {Path.GetDirectoryName(path)})");
        Assert.DoesNotContain("customers", scrubbed);
        Assert.Contains("<file>", scrubbed);
        Assert.Contains("<folder>", scrubbed);
    }
}

/// <summary>The real RPC surface over a fake provider (file profiles need no server).</summary>
public sealed class TestHostWithFake : IAsyncDisposable
{
    public FakeDbProvider Provider { get; } = new();
    public CapturingNotifier Notifier { get; } = new();
    public Host.Services Services { get; }
    public Host.RpcServer Rpc { get; }
    readonly string _dir = Directory.CreateTempSubdirectory("qhostf-").FullName;

    /// <param name="interactiveDeadline">The RPC deadline (default: the shipped one).</param>
    public TestHostWithFake(TimeSpan? interactiveDeadline = null)
    {
        Services = Host.Services.Create(LocalStore.InMemory(), Frames, Notifier, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            new ProviderRegistry([Provider]), _dir);
        Rpc = interactiveDeadline is { } d ? new Host.RpcServer(Services, () => { }) { InteractiveDeadline = d } : new Host.RpcServer(Services, () => { });
    }

    public CapturingFrames Frames { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}
