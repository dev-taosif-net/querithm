using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Logging;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests.Security;

/// <summary>§13: build-time rules that must not be relaxed. Each rule has at least one test here.</summary>
public class SecurityTests
{
    // Rule 1 — secrets live only in the OS keyring.
    [Theory]
    [InlineData("Server=x;User ID=sa;Password=hunter2;Encrypt=True", "hunter2")]
    [InlineData("Server=x;pwd=hunter2", "hunter2")]
    [InlineData("password = 'my secret'; x=1", "my secret")]
    [InlineData("Host=h;Password={a;b}", "a;b")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abc", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("{\"secret\":\"s3cr3t-value\",\"profileId\":\"p\"}", "s3cr3t-value")]
    [InlineData("{\"sshSecret\":\"zq\",\"profileId\":\"p\"}", "zq")]
    [InlineData("{\"passphrase\":\"zq\",\"keyPath\":\"/k\"}", "zq")]
    [InlineData("Access Token=abc.def.ghi;Server=x", "abc.def.ghi")]
    public void Scrubber_masks_secret_patterns(string input, string secret)
    {
        var scrubbed = LogScrubber.Scrub(input);
        Assert.DoesNotContain(secret, scrubbed);
        Assert.Contains("***", scrubbed);
    }

    [Fact]
    public void Scrubber_masks_registered_secret_anywhere()
    {
        LogScrubber.RegisterSecret("Tr0ub4dor&3");
        Assert.Equal("login failed for *** in message", LogScrubber.Scrub("login failed for Tr0ub4dor&3 in message"));
    }

    [Fact]
    public void Profile_dto_has_no_secret_property()
    {
        var names = typeof(Profile).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToArray();
        foreach (var bad in new[] { "password", "pwd", "secret", "token", "accesstoken", "connectionstring" })
            Assert.DoesNotContain(bad, names);
    }

    [Fact]
    public void Profile_table_has_no_password_column()
    {
        var store = LocalStore.InMemory();
        using var c = store.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('profile')";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read()) cols.Add(r.GetString(0));
        Assert.NotEmpty(cols);
        Assert.DoesNotContain(cols, n => n.Contains("pass", StringComparison.OrdinalIgnoreCase) || n.Contains("secret", StringComparison.OrdinalIgnoreCase) || n.Contains("pwd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Saving_a_profile_never_persists_the_secret()
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "x", Engine = Engine.Mssql, Host = "h", Username = "u" });
        await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "super-secret-pw-123" });
        using var c = h.Services.Store.Open();
        foreach (var table in new[] { "profile", "setting", "history", "schema_snapshot" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                    Assert.DoesNotContain("super-secret-pw-123", r.IsDBNull(i) ? "" : r.GetValue(i).ToString());
        }
    }

    [Fact]
    public void Logger_output_is_scrubbed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qlog-" + Guid.NewGuid().ToString("N")[..8]);
        using (var provider = new ScrubbingLoggerProvider(dir, Microsoft.Extensions.Logging.LogLevel.Information))
        {
            var log = provider.CreateLogger("t");
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(log, "connecting with Server=a;Password=pa55word;");
        }
        var text = string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText));
        Assert.DoesNotContain("pa55word", text);
        Directory.Delete(dir, true);
    }

    /// <summary>
    /// Snippet bodies are user data: they stay in store.db. Saving, listing, deleting and every refusal leave no trace of
    /// a body in the sidecar's log (at its most verbose level) or in the error messages sent to the webview.
    /// </summary>
    [Fact]
    public async Task Snippet_bodies_never_reach_logs_or_error_messages()
    {
        const string marker = "zz_private_body_7f3c";
        var dir = Path.Combine(Path.GetTempPath(), "qlog-" + Guid.NewGuid().ToString("N")[..8]);
        var errors = new List<string>();
        using (var provider = new ScrubbingLoggerProvider(dir, LogLevel.Trace))
        using (var logs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider)))
        {
            var providers = new Querithm.Core.Sessions.ProviderRegistry([new SqlServerProvider(), new PostgresProvider()]);
            await using var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(), logs, providers, dir);
            var rpc = new RpcServer(services, () => { });
            Snippet S(string prefix, string body) => new() { Id = "", Title = "t", Prefix = prefix, Body = body };
            var saved = await rpc.SnippetSave(new SnippetSaveParams { Snippet = S("p1", $"SELECT '{marker}';") });
            await rpc.SnippetSave(new SnippetSaveParams { Snippet = saved with { Body = $"SELECT '{marker}', 2;" } });
            await rpc.SnippetList();
            async Task Fails(Snippet s)
            {
                try { await rpc.SnippetSave(new SnippetSaveParams { Snippet = s }); Assert.Fail("expected a refusal"); }
                catch (QuerithmException e) { errors.Add(e.Message); }
            }
            await Fails(S("p1", $"SELECT '{marker}';"));                          // duplicate prefix
            await Fails(S("bad prefix", $"SELECT '{marker}';"));                  // invalid prefix
            await Fails(S("p2", marker + new string('x', SnippetLimits.BodyBytes))); // oversize body
            await Fails(S("p3", $"SELECT '{marker}';\0"));                        // NUL in the body
            await rpc.SnippetDelete(new SnippetRef { Id = saved.Id });
            logs.CreateLogger("probe").LogInformation("probe line");
        }
        var text = string.Concat(Directory.GetFiles(dir, "*.log", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("probe line", text); // the log was really captured
        Assert.DoesNotContain(marker, text);
        Assert.Equal(4, errors.Count);
        Assert.All(errors, m => Assert.DoesNotContain(marker, m));
        Directory.Delete(dir, true);
    }

    // Rule 2 — TLS on by default.
    [Fact]
    public void SqlServer_defaults_to_encryption_without_trusting_certificates()
    {
        var cs = new SqlServerProvider().BuildConnectionString(new Profile { Id = "p", Name = "n", Engine = Engine.Mssql, Host = "h", Username = "u" }, "pw", null);
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
        Assert.NotEqual(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional, b.Encrypt);
        Assert.False(b.TrustServerCertificate);
        Assert.False(b.Pooling);
    }

    [Fact]
    public void Postgres_defaults_to_ssl_require()
    {
        var cs = new PostgresProvider().BuildConnectionString(new Profile { Id = "p", Name = "n", Engine = Engine.Pg, Host = "h", Username = "u" }, "pw", null);
        Assert.Equal(Npgsql.SslMode.Require, new Npgsql.NpgsqlConnectionStringBuilder(cs).SslMode);
    }

    // Rule 4 — the sidecar socket is authenticated.
    [Fact]
    public async Task Data_plane_rejects_wrong_token_and_accepts_right_one()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine("/tmp", $"qt-{Guid.NewGuid():N}"[..12] + ".sock");
        await using var server = new DataPlaneServer(path, "right-token", NullLogger.Instance);
        server.Start();
        for (var i = 0; i < 50 && !File.Exists(path); i++) await Task.Delay(20);
        Assert.True(File.Exists(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path) & (UnixFileMode)0x1FF);

        async Task<byte[]?> TryConnect(string token)
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await s.ConnectAsync(new UnixDomainSocketEndPoint(path));
            await s.SendAsync(Encoding.UTF8.GetBytes(token + "\n"));
            var write = server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 0, ReadOnlyMemory<byte>.Empty, new CancellationTokenSource(1500).Token).AsTask();
            var buf = new byte[64];
            using var cts = new CancellationTokenSource(1500);
            try
            {
                var n = await s.ReceiveAsync(buf, cts.Token);
                return n > 0 ? buf[..n] : null;
            }
            catch (OperationCanceledException) { return null; }
            finally { try { await write; } catch { } }
        }

        Assert.Null(await TryConnect("wrong-token"));
        var frame = await TryConnect("right-token");
        Assert.NotNull(frame);
        Assert.Equal(23, frame!.Length);
        Assert.Equal(FrameKind.EndOfExecution, frame[4]);
    }

    /// <summary>
    /// Windows: a current-user named pipe with the same token check. Regression: the pipe allowed one instance, so after
    /// the first client attached the accept loop died ("All pipe instances are busy") and no client could ever reconnect.
    /// </summary>
    [Fact]
    public async Task Data_plane_pipe_rejects_wrong_token_and_keeps_accepting_after_a_client_attaches()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = $"qt-{Guid.NewGuid():N}"[..16];
        await using var server = new DataPlaneServer(name, "right-token", NullLogger.Instance);
        server.Start();

        async Task<(System.IO.Pipes.NamedPipeClientStream Pipe, byte[]? Frame)> Connect(string token)
        {
            var pipe = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(token + "\n"));
            await pipe.FlushAsync();
            var buf = new byte[64];
            using var cts = new CancellationTokenSource(1500);
            var read = pipe.ReadAsync(buf, cts.Token).AsTask();
            // Frames go to whichever client is attached; keep writing until this one (once authenticated) gets one.
            while (!read.IsCompleted && !cts.IsCancellationRequested)
            {
                try { await server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 0, ReadOnlyMemory<byte>.Empty, cts.Token); } catch { }
                await Task.WhenAny(read, Task.Delay(50));
            }
            try
            {
                var n = await read;
                return (pipe, n > 0 ? buf[..n] : null);
            }
            catch (OperationCanceledException) { return (pipe, null); }
        }

        var (bad, badFrame) = await Connect("wrong-token");
        Assert.Null(badFrame);
        await bad.DisposeAsync();
        var (first, frame) = await Connect("right-token");
        Assert.Equal((23, FrameKind.EndOfExecution), (frame!.Length, frame[4]));
        // While the first client is attached, a wrong token is still refused and a right one takes over (a reconnect).
        var (bad2, bad2Frame) = await Connect("wrong-token");
        Assert.Null(bad2Frame);
        await bad2.DisposeAsync();
        var (second, frame2) = await Connect("right-token");
        Assert.Equal(23, frame2!.Length);
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    // Rule 7 — production guards are enforced in the sidecar.
    [Fact]
    public async Task Prod_first_execution_is_blocked_until_confirmed()
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "Billing", Engine = Engine.Mssql, Host = "prod-host", Environment = EnvironmentKind.Prod });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = s.SessionId, Sql = "SELECT 1" });
        Assert.Null(r.ExecutionId);
        Assert.Equal(GuardReason.ProdFirstRun, r.Blocked!.Reason);
        Assert.Equal("prod-host", r.Blocked.Server);
    }

    // Untrusted RPC input: plan text from a result cell (plan.parse, "Open as plan") is read without DTDs, so neither an
    // entity expansion nor an external entity (a local file, a URL) is ever resolved; the call is refused instead.
    [Theory]
    [InlineData("<!DOCTYPE ShowPlanXML [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]><ShowPlanXML>&c;</ShowPlanXML>")]
    [InlineData("<!DOCTYPE ShowPlanXML [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><ShowPlanXML>&x;</ShowPlanXML>")]
    [InlineData("<!DOCTYPE ShowPlanXML SYSTEM \"http://127.0.0.1:9/plan.dtd\"><ShowPlanXML/>")]
    public async Task Plan_text_is_parsed_without_dtds_or_external_entities(string xml)
    {
        await using var h = new TestHost();
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.PlanParse(new PlanParseParams { Engine = Engine.Mssql, Raw = xml }));
        Assert.Equal("invalid", ex.Code);
        Assert.Contains("DTD", ex.Message);
        Assert.DoesNotContain("root:", ex.Message);
    }

    // Untrusted RPC input: a negative row limit is refused, not read as "unlimited" (§7 Row fetch limits).
    [Theory]
    [InlineData(Engine.Mssql)]
    [InlineData(Engine.Pg)]
    public async Task Negative_row_limit_is_refused_before_anything_runs(Engine engine)
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "Dev", Engine = engine, Host = "dev-host" });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = s.SessionId, Sql = "SELECT 1", Options = new ExecuteOptions { MaxRows = -1 } }));
        Assert.Equal("invalid", ex.Code);
        Assert.Contains("row limit", ex.Message);
    }

    [Theory]
    [InlineData("DELETE FROM dbo.Orders", "deleteWithoutWhere")]
    [InlineData("UPDATE dbo.Orders SET Status = 1", "updateWithoutWhere")]
    [InlineData("DROP TABLE dbo.Orders", "drop")]
    [InlineData("TRUNCATE TABLE dbo.Orders", "truncate")]
    [InlineData("ALTER TABLE dbo.Orders ADD x int", "alter")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1", "alter")]
    [InlineData("SELECT 1 FROM", "unparsed")]
    public async Task Destructive_statement_on_prod_needs_second_confirmation(string sql, string rule)
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "Billing", Engine = Engine.Mssql, Host = "prod", Environment = EnvironmentKind.Prod });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = s.SessionId, Sql = sql, Options = new ExecuteOptions { ConfirmedProd = true } });
        Assert.Null(r.ExecutionId);
        Assert.Equal(GuardReason.Destructive, r.Blocked!.Reason);
        Assert.Contains(r.Blocked.Findings, f => f.Rule == rule);
    }

    [Theory]
    [InlineData("DELETE FROM dbo.Orders WHERE OrderId = 5")]
    [InlineData("UPDATE o SET Status = 1 FROM dbo.Orders o WHERE o.OrderId = 5")]
    [InlineData("SELECT * FROM dbo.Orders")]
    [InlineData("CREATE PROCEDURE dbo.p AS DELETE FROM dbo.Orders")]
    public async Task Safe_statements_on_prod_pass_the_destructive_guard(string sql)
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "Billing", Engine = Engine.Mssql, Host = "127.0.0.1", Port = 1, TimeoutS = 1, Environment = EnvironmentKind.Prod });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = s.SessionId, Sql = sql, Options = new ExecuteOptions { ConfirmedProd = true } });
        Assert.Null(r.Blocked);
        Assert.NotNull(r.ExecutionId); // proceeds to connect (and fails: no server) — the guard did not block
    }

    [Fact]
    public async Task Postgres_destructive_statements_are_guarded_on_prod()
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "PG prod", Engine = Engine.Pg, Host = "prod", Environment = EnvironmentKind.Prod });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams
        {
            SessionId = s.SessionId, Sql = "WITH x AS (SELECT 1) DELETE FROM orders; SELECT 'DELETE FROM t'", Options = new ExecuteOptions { ConfirmedProd = true },
        });
        Assert.Equal(GuardReason.Destructive, r.Blocked!.Reason);
        Assert.Single(r.Blocked.Findings);
    }

    // Rule 7 for Querithm-generated writes: grid patches, imports and syncs are guarded in the sidecar.
    [Theory]
    [InlineData(Engine.Mssql)]
    [InlineData(Engine.Pg)]
    public async Task Tool_writes_on_prod_need_confirmation_every_time(Engine engine)
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "prod", Engine = engine, Host = "prod", Environment = EnvironmentKind.Prod });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var session = h.Services.Sessions.Get(s.SessionId);
        h.Services.Sessions.ConfirmProd(session); // past the first-run confirm: tool writes still ask
        DestructiveFinding[] none = [];
        DestructiveFinding[] del = [new DestructiveFinding { Rule = "delete", Statement = "DELETE 1 row(s)", Description = "Deletes 1 row(s)" }];
        Assert.Equal(GuardReason.ProdWrite, h.Services.Executions.ToolGuard(session, false, false, none, none)?.Reason);
        Assert.Equal(GuardReason.Destructive, h.Services.Executions.ToolGuard(session, true, false, none, del)?.Reason);
        Assert.Null(h.Services.Executions.ToolGuard(session, true, true, none, del));
    }

    [Fact]
    public async Task Grid_patches_for_results_the_sidecar_did_not_record_are_refused()
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "dev", Engine = Engine.Mssql, Host = "h" });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = s.SessionId, ExecutionId = Ulid.New().ToString(),
            Updates = [new GridRowUpdate { Key = [new GridValue { Text = "1" }], Changes = new() { ["name"] = new GridValue { Text = "x" } }, Original = [] }],
        }));
        Assert.Equal("resultClosed", ex.Code);
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("name]; DROP TABLE x; --")]
    [InlineData("\"name\"")]
    public void Grid_patches_cannot_name_columns_outside_the_recorded_table(string column)
    {
        var edit = new ResultEditInfo
        {
            Editable = true, Schema = "dbo", Table = "t", KeyColumns = ["id"], KeyOrdinals = [0],
            Columns = [new EditColumn { Ordinal = 0, Column = "id", Type = "int", Key = true, Comparable = true }, new EditColumn { Ordinal = 1, Column = "name", Type = "nvarchar(10)", Nullable = true, Comparable = true }],
        };
        var patch = new GridPatchParams
        {
            SessionId = "s", ExecutionId = "e",
            Updates = [new GridRowUpdate { Key = [new GridValue { Text = "1" }], Changes = new() { [column] = new GridValue { Text = "x" } }, Original = new() { [column] = new GridValue { Text = "y" } } }],
        };
        foreach (var dialect in new Querithm.Contract.Providers.IDataEditDialect[] { new Querithm.Providers.SqlServer.SqlServerDataEdit(), new PostgresDataEdit() })
            Assert.StartsWith("Unknown column", Assert.Throws<QuerithmException>(() => Querithm.Core.Execution.PatchBuilder.Build(dialect, edit, patch, false)).Message);
    }

    // Plan M5 — migration scripts are built only from the sidecar's own comparison: keys and renames are looked up, never trusted.
    [Fact]
    public async Task Schema_migrations_accept_only_items_and_renames_of_the_comparison()
    {
        await using var h = new TestHost();
        var d = new SqlServerSchemaCompare();
        Querithm.Contract.Providers.SchemaModel M(string col) => new()
        {
            Engine = Engine.Mssql, MajorVersion = 16, Database = "db", Schemas = ["dbo"], Modules = [],
            Tables = [new Querithm.Contract.Providers.ModelTable { Schema = "dbo", Name = "t", Columns = [new() { Name = "id", Type = "int" }, new() { Name = col, Type = "date", Nullable = true }] }],
        };
        var cmp = Querithm.Core.Compare.SchemaComparer.Compare(M("b"), M("a"), d);
        var info = new CompareSideInfo { Label = "x", Live = true };
        var id = h.Services.SchemaCompare.Remember(cmp, info, info, null);
        var key = cmp.Items.Single(i => i.Kind == CompareObjectKind.Table).Key;

        var unknown = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SchemaCompareMigrationSql(new SchemaMigrationParams { CompareId = id, Keys = ["Table:dbo.t; DROP TABLE x"] }));
        Assert.Equal("invalid", unknown.Code);
        var forged = new PossibleRename { TableKey = key, From = "a", To = "b]; DROP TABLE x; --", Type = "date" };
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            h.Rpc.SchemaCompareMigrationSql(new SchemaMigrationParams { CompareId = id, Keys = [key], Renames = [forged] }))).Code);
        Assert.Equal("compareGone", (await Assert.ThrowsAsync<QuerithmException>(() =>
            h.Rpc.SchemaCompareObjectSql(new CompareObjectParams { CompareId = Ulid.New().ToString(), Key = key }))).Code);
        // The offered rename is accepted, and the script only describes; nothing runs on a server.
        var ok = await h.Rpc.SchemaCompareMigrationSql(new SchemaMigrationParams { CompareId = id, Keys = [key], Renames = [cmp.Renames.Single()] });
        Assert.Contains("sp_rename", ok.Sql);
    }

    // Plan M6 — data compare names tables only through the sidecar's own metadata and runs no query that writes.
    [Theory]
    [InlineData("t]; DROP TABLE x; --", null)]
    [InlineData("t", "DELETE FROM dbo.t")]
    [InlineData("t", "SELECT 1 AS id; DROP TABLE dbo.t")]
    public async Task Data_compare_sides_are_looked_up_and_queries_are_read_only(string table, string? query)
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "dev", Engine = Engine.Mssql, Host = "h" });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x", Database = "db" });
        var side = query == null ? new DataCompareSide { SessionId = s.SessionId, Schema = "dbo", Table = table } : new DataCompareSide { SessionId = s.SessionId, Query = query };
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.DataCompareStart(new DataCompareParams { Source = side, Target = side, Key = ["id"] }));
        Assert.Equal(query == null ? "compareTable" : "compareQuery", ex.Code);
        Assert.Equal("compareGone", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.DataCompareSyncSql(new DataSyncSqlParams { CompareId = Ulid.New().ToString() }))).Code);
    }

    // Plan M7 — renames name only objects of the sidecar's snapshot, and the new name only ever appears quoted or as a literal.
    [Theory]
    [InlineData(Engine.Mssql, "EXEC sp_rename N'[dbo].[t]', N'x]; DROP TABLE t; --';")]
    [InlineData(Engine.Pg, "ALTER TABLE \"public\".\"t\" RENAME TO \"x]; DROP TABLE t; --\";")]
    public async Task Renames_quote_the_new_name_and_need_a_known_target(Engine engine, string expected)
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "dev", Engine = engine, Host = "h" });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x", Database = "db" });
        var schema = engine == Engine.Mssql ? "dbo" : "public";
        h.Services.Metadata.Store.ApplyRefresh(p.Id, "db", true, [new Querithm.Contract.Providers.ObjectStamp(1, schema, "t", ObjectKind.Table, 1, null)], [],
            new Querithm.Contract.Providers.MetadataBundle([new Querithm.Contract.Providers.ColumnRow(1, 1, "id", "int", null, null, null, false, false, false, null)], [], [], []), "1");
        h.Services.Metadata.Cache.Invalidate(p.Id, "db");
        RenameParams R(string table, string name) => new() { SessionId = s.SessionId, Database = "db", Target = new RefactorTarget { Kind = RefactorKind.Table, Schema = schema, Name = table }, NewName = name };

        Assert.Equal("refactorTarget", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.RefactorPreviewRename(R("t]; DROP TABLE x; --", "n")))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.RefactorPreviewRename(R("t", "a\u0000b")))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.RefactorPreviewRename(R("t", "  ")))).Code);
        var ok = await h.Rpc.RefactorPreviewRename(R("t", "x]; DROP TABLE t; --"));
        Assert.Contains(expected, ok.Sql);
    }

    // Rule 5/6 — no TCP listener is ever bound by the sidecar.
    [Fact]
    public void Host_assembly_never_binds_tcp()
    {
        var host = typeof(DataPlaneServer).Assembly;
        var refs = host.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("Microsoft.AspNetCore.Server.Kestrel", refs);
        var source = typeof(DataPlaneServer).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Select(m => m.Name);
        Assert.Contains("AcceptLoopAsync", source);
    }

    // Rule 6 — the shell's updater gives the webview no network or updater permission, and the CSP stays local.
    [Fact]
    public void Webview_has_no_network_updater_or_process_permission()
    {
        var caps = File.ReadAllText(Path.Combine(RepoRoot(), "src-tauri", "capabilities", "default.json"));
        foreach (var forbidden in new[] { "\"http:", "\"updater:", "\"process:", "\"shell:" })
            Assert.DoesNotContain(forbidden, caps);
        using var conf = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src-tauri", "tauri.conf.json")));
        var csp = conf.RootElement.GetProperty("app").GetProperty("security").GetProperty("csp").GetString()!;
        Assert.DoesNotContain("https:", csp);
        Assert.DoesNotContain("github", csp);
    }

    // Rule 10 — updates are signed, bound to their version and fetched over HTTPS only.
    [Fact]
    public void Updater_accepts_only_signed_versioned_packages_over_https()
    {
        using var conf = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src-tauri", "tauri.conf.json")));
        var updater = conf.RootElement.GetProperty("plugins").GetProperty("updater");
        Assert.True(updater.GetProperty("requireSignedVersion").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(updater.GetProperty("pubkey").GetString()));
        var endpoints = updater.GetProperty("endpoints").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, e => Assert.StartsWith("https://", e));
        foreach (var unsafeFlag in new[] { "dangerousInsecureTransportProtocol", "dangerousAcceptInvalidCerts", "dangerousAcceptInvalidHostnames", "allowDowngrades" })
            Assert.False(updater.TryGetProperty(unsafeFlag, out var v) && v.GetBoolean(), unsafeFlag);
        Assert.True(conf.RootElement.GetProperty("bundle").GetProperty("createUpdaterArtifacts").GetBoolean());
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src-tauri", "tauri.conf.json"))) return d.FullName;
        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}
