using System.IO.Pipelines;
using System.Text.Json;
using System.Text.RegularExpressions;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Ssh;
using Querithm.Host;
using StreamJsonRpc;

namespace Querithm.Tests.Security;

/// <summary>
/// What script running in the webview can reach (audit 2026-11-02: H7, M1, M2, M4). The webview is untrusted: its fs
/// scopes stay off the sidecar's private state (§13 rule 6), the control plane binds only the names the shell owns as
/// the shell spells them (§5), a host key is trusted only if a bastion presented it, and the CSP allows no form, base,
/// object or frame (§13 rule 5). The shell's half of M1 (refusing case variants) is tested in src-tauri (injection.rs).
/// </summary>
public sealed class WebviewBoundarySecurityTests
{
    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src-tauri", "tauri.conf.json"))) return d.FullName;
        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }

    // ------------------------------------------------------------------------------------------- rule 6 (H7)

    /// <summary>The fs permissions the webview holds; anything else (a preset such as fs:allow-appdata-write-recursive, fs:scope) widens every command.</summary>
    static readonly string[] FsPermissions = ["fs:allow-read-text-file", "fs:allow-write-text-file", "fs:allow-write-file", "fs:allow-mkdir", "fs:allow-remove", "fs:allow-rename", "fs:allow-read-dir"];
    static readonly string[] Writes = ["fs:allow-write-text-file", "fs:allow-write-file", "fs:allow-mkdir", "fs:allow-remove", "fs:allow-rename"];

    /// <summary>Sidecar-private state under the data folder: native code, the store, trusted host keys, logs, DuckDB's folders, exports it writes.</summary>
    static readonly string[] Private =
    [
        "$DATA/querithm/native", "$DATA/querithm/native/querithm-sidecar/abc/libduckdb.so", "$DATA/querithm/native/x/e_sqlite3.dll",
        "$DATA/querithm/store.db", "$DATA/querithm/store.db-wal", "$DATA/querithm/store.db-shm", "$DATA/querithm/store.db-journal",
        "$DATA/querithm/known_hosts", "$DATA/querithm/logs", "$DATA/querithm/logs/sidecar.log", "$DATA/querithm/logs/shell-20261103.log",
        "$DATA/querithm/store.db.v3.bak", "$DATA/querithm/store.db.corrupt-20261103T101500Z", "$DATA/querithm/instance.lock",
        "$DATA/querithm/duckdb/none/x.duckdb_extension", "$DATA/querithm/duckdb/tmp/1/spill", "$DATA/querithm/exports/sessions.csv",
        "$DATA/querithm/grants.json", "$DATA/querithm/x", "$DATA/querithm/scripts/sub/x.sql",
    ];

    /// <summary>Tauri's scope glob (require_literal_separator): `*` and `?` stay inside one path segment, `**` crosses them.</summary>
    static bool Matches(string pattern, string path)
    {
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]").Replace("\u0001", ".*") + "$";
        return Regex.IsMatch(path, rx);
    }

    static List<(string Id, string[] Allow)> Permissions()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src-tauri", "capabilities", "default.json")));
        var list = new List<(string, string[])>();
        foreach (var p in doc.RootElement.GetProperty("permissions").EnumerateArray())
        {
            if (p.ValueKind == JsonValueKind.String) { list.Add((p.GetString()!, [])); continue; }
            var allow = p.TryGetProperty("allow", out var a) ? a.EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToArray() : [];
            list.Add((p.GetProperty("identifier").GetString()!, allow));
        }
        return list;
    }

    /// <summary>Beyond core:default the webview may only set its own page zoom (the Interface scale setting, L26 / §16).</summary>
    [Fact]
    public void Webview_core_permissions_add_only_its_own_page_zoom()
    {
        var core = Permissions().Select(p => p.Id).Where(id => id.StartsWith("core:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["core:default", "core:webview:allow-set-webview-zoom"], core);
    }

    [Fact]
    public void Webview_fs_write_scopes_never_reach_the_sidecars_native_code_store_keys_or_logs()
    {
        var perms = Permissions();
        foreach (var (id, _) in perms.Where(p => p.Id.StartsWith("fs:", StringComparison.Ordinal)))
            Assert.Contains(id, FsPermissions);
        foreach (var (id, allow) in perms.Where(p => Writes.Contains(p.Id)))
        foreach (var pattern in allow)
        {
            Assert.StartsWith("$DATA/querithm/", pattern + "/");
            Assert.DoesNotContain("**", pattern);
            foreach (var path in Private) Assert.False(Matches(pattern, path), $"{id} scope {pattern} covers {path}");
        }
        // What the webview writes, and nothing more: tab recovery copies + the workspace, the app bench's result.
        bool Allows(string id, string path) => perms.Any(p => p.Id == id && p.Allow.Any(a => Matches(a, path)));
        Assert.True(Allows("fs:allow-write-text-file", "$DATA/querithm/scripts/01HX.sql"));
        Assert.True(Allows("fs:allow-write-text-file", "$DATA/querithm/scripts/01HX.sqlbook"));
        Assert.True(Allows("fs:allow-write-text-file", "$DATA/querithm/scripts/workspace.json"));
        Assert.True(Allows("fs:allow-write-text-file", "$DATA/querithm/bench/app-1.json"));
        Assert.True(Allows("fs:allow-mkdir", "$DATA/querithm/scripts"));
        Assert.True(Allows("fs:allow-remove", "$DATA/querithm/scripts/01HX.sql"));
        Assert.False(Allows("fs:allow-remove", "$DATA/querithm/bench/app-1.json"));
        // M23: atomic writes rename a temporary file into place, and restore lists the scripts folder (only that folder).
        Assert.True(Allows("fs:allow-rename", "$DATA/querithm/scripts/workspace.json"));
        Assert.Equal(["$DATA/querithm/scripts/*"], perms.Single(p => p.Id == "fs:allow-rename").Allow);
        Assert.Equal(["$DATA/querithm/scripts"], perms.Single(p => p.Id == "fs:allow-read-dir").Allow);
        // Binary writes go only to a path the user picked in the save dialog (the dialog adds it to the scope at run time).
        Assert.Empty(perms.Single(p => p.Id == "fs:allow-write-file").Allow);
        Assert.True(Allows("fs:allow-read-text-file", "$DATA/querithm/exports/sessions.csv"));
        Assert.False(Allows("fs:allow-read-text-file", "$DATA/querithm/store.db"));
        Assert.False(Allows("fs:allow-read-text-file", "$DATA/querithm/known_hosts"));
    }

    [Fact]
    public void Native_libraries_are_extracted_outside_every_webview_scope()
    {
        var sidecar = File.ReadAllText(Path.Combine(RepoRoot(), "src-tauri", "src", "sidecar.rs"));
        var line = sidecar.Split('\n').Single(l => l.Contains("\"DOTNET_BUNDLE_EXTRACT_BASE_DIR\"", StringComparison.Ordinal));
        Assert.DoesNotContain("data_dir", line);
        Assert.Contains("app_local_data_dir()", sidecar);
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src-tauri", "capabilities", "default.json"));
        foreach (var variable in new[] { "$APPLOCALDATA", "$LOCALDATA", "$APPDATA", "$APPCONFIG", "$CONFIG", "$HOME", "$RESOURCE", "$TEMP" })
            Assert.DoesNotContain(variable, text);
    }

    // ------------------------------------------------------------------------------------------- rule 5 (M4)

    [Fact]
    public void Both_webview_csps_allow_no_form_target_base_object_or_frame()
    {
        using var conf = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src-tauri", "tauri.conf.json")));
        var security = conf.RootElement.GetProperty("app").GetProperty("security");
        foreach (var key in new[] { "csp", "devCsp" })
        {
            var csp = security.GetProperty(key).GetString()!;
            foreach (var d in new[] { "form-action 'none'", "base-uri 'none'", "object-src 'none'", "frame-src 'none'" })
                Assert.Contains(d, csp);
            var script = csp.Split(';').Select(x => x.Trim()).Single(x => x.StartsWith("script-src ", StringComparison.Ordinal));
            Assert.DoesNotContain("'unsafe-inline'", script);
            Assert.DoesNotContain("'unsafe-eval'", script);
        }
    }

    // ------------------------------------------------------------------------------------------- §5 (M1)

    [Fact]
    public void Rpc_options_never_bind_a_case_variant_of_a_shell_owned_name()
    {
        var o = Json.CreateRpc();
        Assert.Null(JsonSerializer.Deserialize<ImportInspectParams>("""{"fileToken":"t","FilePath":"/etc/passwd"}""", o)!.FilePath);
        Assert.Null(JsonSerializer.Deserialize<ImportRunParams>("""{"sessionId":"s","inspectionId":"i","fileToken":"t","schema":"dbo","table":"x","FILEPATH":"/etc/passwd"}""", o)!.FilePath);
        var side = JsonSerializer.Deserialize<CompareSide>("""{"FileToken":"t","FilePath":"/etc/passwd"}""", o)!;
        Assert.Null(side.FileToken);
        Assert.Null(side.FilePath);
        const string grant = """{"grantId":"g","path":"/etc/passwd","mode":"readWrite","identity":"u:1:2"}""";
        var open = JsonSerializer.Deserialize<SessionOpenParams>($$"""{"profileId":"p","FileGrant":{{grant}},"Secret":"s","SSHSECRET":"k"}""", o)!;
        Assert.Null(open.FileGrant);
        Assert.Null(open.Secret);
        Assert.Null(open.SshSecret);
        Assert.Null(JsonSerializer.Deserialize<ProfileTestParams>($$"""{"profileId":"p","fileGRANT":{{grant}}}""", o)!.FileGrant);
        // The names as the contract and the shell spell them still bind.
        Assert.Equal("/a.csv", JsonSerializer.Deserialize<ImportInspectParams>("""{"fileToken":"t","filePath":"/a.csv"}""", o)!.FilePath);
        Assert.Equal("/etc/passwd", JsonSerializer.Deserialize<SessionOpenParams>($$"""{"profileId":"p","fileGrant":{{grant}}}""", o)!.FileGrant!.Path);
        // Stored settings and saved schema files keep reading case-insensitively.
        Assert.Equal("/etc/passwd", JsonSerializer.Deserialize<ImportInspectParams>("""{"fileToken":"t","FilePath":"/etc/passwd"}""", Json.Create())!.FilePath);
    }

    [Fact]
    public async Task A_case_variant_filePath_sent_over_the_control_plane_never_reaches_the_import()
    {
        await using var h = new TestHost();
        var csv = Path.Combine(h.DataDir, "picked.csv");
        await File.WriteAllTextAsync(csv, "id,name\n1,a\n");
        var a = new Pipe();
        var b = new Pipe();
        using var server = new QuerithmJsonRpc(new NewLineDelimitedMessageHandler(b.Writer.AsStream(), a.Reader.AsStream(), QuerithmJsonRpc.Formatter()));
        server.AddMethods(h.Rpc);
        server.StartListening();
        using var client = new JsonRpc(new NewLineDelimitedMessageHandler(a.Writer.AsStream(), b.Reader.AsStream(), new SystemTextJsonFormatter()));
        client.StartListening();

        foreach (var key in new[] { "FilePath", "FILEPATH", "filepath" })
        {
            var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                client.InvokeWithParameterObjectAsync<JsonElement>("import.inspect", new Dictionary<string, object?> { ["fileToken"] = "t", [key] = csv }));
            Assert.Contains("\"fileToken\"", ex.ErrorData?.ToString() ?? "");
        }
        // The path the shell injects (exact name) is read.
        var ok = await client.InvokeWithParameterObjectAsync<JsonElement>("import.inspect", new Dictionary<string, object?> { ["fileToken"] = "t", ["filePath"] = csv });
        Assert.Equal(1, ok.GetProperty("rowCount").GetInt64());
    }

    // ------------------------------------------------------------------------------------------- L10

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bench_methods_exist_only_when_the_harness_asks_for_them(bool bench)
    {
        await using var h = new TestHost();
        var a = new Pipe();
        var b = new Pipe();
        using var server = new QuerithmJsonRpc(new NewLineDelimitedMessageHandler(b.Writer.AsStream(), a.Reader.AsStream(), QuerithmJsonRpc.Formatter()));
        server.AddMethods(h.Rpc, bench);
        server.StartListening();
        using var client = new JsonRpc(new NewLineDelimitedMessageHandler(a.Writer.AsStream(), b.Reader.AsStream(), new SystemTextJsonFormatter()));
        client.StartListening();
        var call = client.InvokeWithParameterObjectAsync<JsonElement>("bench.synthetic", new Dictionary<string, object?> { ["rows"] = 1, ["columns"] = 1 });
        if (bench) Assert.True((await call).TryGetProperty("executionId", out _));
        else await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => call);
        // Everything else is there either way.
        Assert.True((await client.InvokeAsync<JsonElement>("system.ping")).TryGetProperty("pid", out _));
    }

    // ------------------------------------------------------------------------------------------- M2

    static SshHostKey Key(string fp = "AAAA", string host = "bastion", int port = 22, string algorithm = "ssh-ed25519") =>
        new() { Host = host, Port = port, Algorithm = algorithm, Fingerprint = fp };

    [Fact]
    public async Task Trust_host_key_accepts_only_a_key_a_bastion_presented()
    {
        await using var h = new TestHost();
        var tunnels = h.Services.Sessions.Tunnels!;
        tunnels.Presented.Record(Key() with { PreviousFingerprint = "OLD" });
        foreach (var forged in new[] { Key("BBBB"), Key(host: "attacker.example"), Key(port: 2222), Key(algorithm: "rsa-sha2-512"), Key("aaaa") })
            Assert.Equal("sshHostKeyNotPresented", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SshTrustHostKey(forged))).Code);
        Assert.False(File.Exists(Path.Combine(h.DataDir, "known_hosts")));
        Assert.Equal(HostKeyVerdict.Unknown, tunnels.KnownHosts.Check("attacker.example", 22, "ssh-ed25519", "AAAA").Verdict);

        await h.Rpc.SshTrustHostKey(Key() with { Host = "BASTION" });
        Assert.Equal(HostKeyVerdict.Trusted, tunnels.KnownHosts.Check("bastion", 22, "ssh-ed25519", "AAAA").Verdict);
        // Used up: a replay of the same call is refused.
        Assert.Equal("sshHostKeyNotPresented", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SshTrustHostKey(Key()))).Code);
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Presented_host_keys_expire_and_stay_bounded()
    {
        var clock = new Clock();
        var keys = new PresentedHostKeys(clock);
        keys.Record(Key());
        clock.Now += PresentedHostKeys.Lifetime - TimeSpan.FromSeconds(1);
        keys.Record(Key("CCCC"));
        Assert.True(keys.Take(Key()));
        keys.Record(Key());
        clock.Now += PresentedHostKeys.Lifetime;
        Assert.False(keys.Take(Key()));
        Assert.False(keys.Take(Key("CCCC")));

        for (var i = 0; i <= PresentedHostKeys.Capacity; i++) keys.Record(Key($"K{i}"));
        Assert.False(keys.Take(Key("K0")));
        Assert.True(keys.Take(Key($"K{PresentedHostKeys.Capacity}")));
        Assert.True(keys.Take(Key("K1")));
    }
}
