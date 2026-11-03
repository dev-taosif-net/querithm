using System.Text.Json;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Store;

namespace Querithm.Tests;

/// <summary>The local SQLite store (§8), on a fresh in-memory database per test.</summary>
public class ProfileStoreTests
{
    readonly ProfileStore _profiles = new(LocalStore.InMemory());

    static Profile New(string name, string? group = null) => new() { Id = "", Name = name, Engine = Engine.Mssql, GroupName = group };

    [Fact]
    public void Save_assigns_an_id_and_defaults()
    {
        var p = _profiles.Save(New("a"));
        Assert.Equal(26, p.Id.Length);
        Assert.Equal(15, p.TimeoutS);
        Assert.Equal(0, p.CommandTimeoutS);
        Assert.True(p.CreatedAt > 0);
        Assert.Equal(p.CreatedAt, p.UpdatedAt);
    }

    [Theory]
    [InlineData("once", null)]
    [InlineData("session", null)]
    [InlineData("duration", 30)]
    [InlineData("forever", null)]
    [InlineData(null, null)]
    public void Password_retention_roundtrips(string? policy, int? minutes)
    {
        var p = _profiles.Save(New("retention") with { PasswordRetention = policy, PasswordRetentionMinutes = minutes });
        var loaded = _profiles.Get(p.Id)!;
        Assert.Equal(policy, loaded.PasswordRetention);
        Assert.Equal(minutes, loaded.PasswordRetentionMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(525601)]
    public void Invalid_password_duration_is_rejected(int minutes) =>
        Assert.Throws<ArgumentException>(() => _profiles.Save(New("bad") with { PasswordRetention = "duration", PasswordRetentionMinutes = minutes }));

    [Fact]
    public void Every_field_roundtrips()
    {
        var saved = _profiles.Save(new Profile
        {
            Id = "", Name = "Full", Engine = Engine.Pg, Host = "db", Port = 6432, Auth = AuthKind.EntraToken, Username = "u", DatabaseDefault = "app",
            Environment = EnvironmentKind.Staging, Color = "#f00", GroupName = "G", TlsMode = TlsMode.VerifyFull, TrustCert = true, CaPath = "/ca.pem",
            TimeoutS = 7, CommandTimeoutS = 90, Options = new() { ["appName"] = "etl", ["applicationIntent"] = "readOnly" },
        });
        var back = _profiles.Get(saved.Id)!;
        Assert.Equal(saved.Options, back.Options);
        Assert.Equal(saved with { Options = back.Options }, back);
    }

    [Fact]
    public void Stored_options_keep_the_reflection_default_format()
    {
        // options_json rows were written by reflection with default options; the source-generated metadata must read and write the same JSON.
        var o = new ProfileStore.StoredOptions(new() { ["appName"] = "etl" }, null, 30);
        Assert.Equal(JsonSerializer.Serialize(o), JsonSerializer.Serialize(o, StoreJsonContext.Default.StoredOptions));
        var legacy = JsonSerializer.Deserialize("""{"Options":{"appName":"etl"},"CaPath":"/ca.pem","CommandTimeoutS":30}""", StoreJsonContext.Default.StoredOptions)!;
        Assert.Equal(("etl", "/ca.pem", 30), (legacy.Options!["appName"], legacy.CaPath, legacy.CommandTimeoutS));
        Assert.Equal(JsonSerializer.Serialize(new[] { "a", "b" }), JsonSerializer.Serialize(["a", "b"], StoreJsonContext.Default.StringArray));
    }

    [Fact]
    public void Update_keeps_created_at_and_replaces_fields()
    {
        var first = _profiles.Save(New("a"));
        var second = _profiles.Save(first with { Name = "b", Host = null, CreatedAt = first.CreatedAt });
        Assert.Equal(first.Id, second.Id);
        var back = Assert.Single(_profiles.List());
        Assert.Equal(("b", (string?)null, first.CreatedAt), (back.Name, back.Host, back.CreatedAt));
    }

    [Fact]
    public void Explicit_id_is_kept()
    {
        Assert.Equal("my-id", _profiles.Save(New("x") with { Id = "my-id" }).Id);
        Assert.NotNull(_profiles.Get("my-id"));
    }

    [Fact]
    public void Get_of_unknown_id_is_null() => Assert.Null(_profiles.Get("nope"));

    [Fact]
    public void List_orders_by_group_then_name()
    {
        _profiles.Save(New("z", "B"));
        _profiles.Save(New("a", "B"));
        _profiles.Save(New("m", "A"));
        _profiles.Save(New("q"));
        Assert.Equal(["q", "m", "a", "z"], _profiles.List().Select(p => p.Name));
    }

    [Fact]
    public void Delete_removes_the_profile_and_its_snapshot_rows()
    {
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var p = profiles.Save(New("a"));
        using (var c = store.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO obj (profile_id, database, object_id, name) VALUES ($p, 'db', 1, 't'), ('other', 'db', 1, 't')";
            cmd.Parameters.AddWithValue("$p", p.Id);
            cmd.ExecuteNonQuery();
        }
        profiles.Delete(p.Id);
        Assert.Null(profiles.Get(p.Id));
        using var c2 = store.Open();
        using var count = c2.CreateCommand();
        count.CommandText = "SELECT group_concat(profile_id) FROM obj";
        Assert.Equal("other", count.ExecuteScalar());
    }

    [Fact]
    public void Unknown_enum_text_in_the_table_falls_back()
    {
        var store = LocalStore.InMemory();
        using (var c = store.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO profile (id, name, engine, auth, environment, tls_mode) VALUES ('x', 'n', 'oracle', 'magic', 'mars', '???')";
            cmd.ExecuteNonQuery();
        }
        var p = new ProfileStore(store).Get("x")!;
        Assert.Equal((Engine.Mssql, AuthKind.Sql, EnvironmentKind.Dev, TlsMode.Default), (p.Engine, p.Auth, p.Environment, p.TlsMode));
        Assert.Null(p.Options);
        Assert.Equal(0, p.CreatedAt);
    }
}

public class SettingsStoreTests
{
    readonly SettingsStore _settings = new(LocalStore.InMemory());

    [Fact]
    public void Invalid_json_is_rejected_and_nothing_is_stored()
    {
        Assert.ThrowsAny<JsonException>(() => _settings.Set("k", "{not json"));
        Assert.Empty(_settings.Get(null));
    }

    [Fact]
    public void Set_overwrites_and_get_filters_by_key()
    {
        _settings.Set("a", "1");
        _settings.Set("b", "\"x\"");
        _settings.Set("a", "2");
        Assert.Equal(2, _settings.Get(null).Length);
        Assert.Equal("2", Assert.Single(_settings.Get(["a", "missing"])).ValueJson);
        Assert.Empty(_settings.Get([]));
    }

    [Fact]
    public void Typed_read_and_write_use_the_wire_format()
    {
        _settings.Write("g", new ProfileGroup { Name = "Ops", Environment = EnvironmentKind.Prod }, Json.Context.ProfileGroup);
        Assert.Equal("""{"name":"Ops","environment":"prod"}""", Assert.Single(_settings.Get(["g"])).ValueJson);
        Assert.Equal(EnvironmentKind.Prod, _settings.Read("g", Json.Context.ProfileGroup)!.Environment);
        Assert.Null(_settings.Read("missing", Json.Context.ProfileGroup));
        Assert.Null(_settings.Read("missing", Json.Context.ProfileGroupArray));
    }
}

public class HistoryStoreTests
{
    readonly HistoryStore _history = new(LocalStore.InMemory());

    [Fact]
    public void List_is_newest_first_and_filters()
    {
        var a = _history.Add("p1", "db1", "SELECT 1", 1.9, null, "done", null);
        var b = _history.Add("p1", "db2", "SELECT 2", 2, 3, "failed", "boom");
        var c = _history.Add("p2", "db1", "SELECT 3", 3, null, "done", null);
        Assert.Equal([c, b, a], _history.List(new HistoryListParams()).Select(h => h.Id));
        Assert.Equal([b, a], _history.List(new HistoryListParams { ProfileId = "p1" }).Select(h => h.Id));
        Assert.Equal([c, a], _history.List(new HistoryListParams { Database = "db1" }).Select(h => h.Id));
        var failed = _history.List(new HistoryListParams { Database = "db2" }).Single();
        Assert.Equal(("failed", "boom", 3L, 2d), (failed.Status, failed.Error, failed.RowsAffected, failed.ElapsedMs));
        Assert.Equal(1d, _history.List(new HistoryListParams { BeforeId = b }).Single().ElapsedMs); // stored as whole ms
    }

    [Fact]
    public void Paging_with_before_id_and_limit_clamp()
    {
        var ids = Enumerable.Range(0, 5).Select(i => _history.Add(null, null, $"SELECT {i}", 0, null, "done", null)).ToArray();
        Assert.Equal([ids[2], ids[1]], _history.List(new HistoryListParams { BeforeId = ids[3], Limit = 2 }).Select(h => h.Id));
        Assert.Single(_history.List(new HistoryListParams { Limit = 0 }));
        Assert.Single(_history.List(new HistoryListParams { Limit = -5 }));
    }

    [Fact]
    public void Search_with_blank_text_lists_recent_entries()
    {
        _history.Add("p", null, "SELECT 1", 0, null, "done", null);
        _history.Add("q", null, "SELECT 2", 0, null, "done", null);
        Assert.Equal(2, _history.Search(new HistorySearchParams { Text = "   " }).Length);
        Assert.Single(_history.Search(new HistorySearchParams { Text = "", ProfileId = "q" }));
    }

    [Fact]
    public void Search_terms_are_prefix_matched_and_all_required()
    {
        _history.Add("p", null, "SELECT * FROM dbo.Customers", 0, null, "done", null);
        _history.Add("p", null, "SELECT * FROM dbo.Orders", 0, null, "done", null);
        _history.Add("q", null, "DELETE FROM dbo.Customers WHERE Id = 1", 0, null, "done", null);
        Assert.Equal(2, _history.Search(new HistorySearchParams { Text = "cust" }).Length);
        Assert.Single(_history.Search(new HistorySearchParams { Text = "select cust" }));
        Assert.Single(_history.Search(new HistorySearchParams { Text = "cust", ProfileId = "q" }));
        Assert.Empty(_history.Search(new HistorySearchParams { Text = "cust nomatch" }));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("a\"b")]
    [InlineData("* OR")]
    [InlineData("NOT AND")]
    [InlineData("(")]
    [InlineData("col:x")]
    public void Fts_operators_in_search_text_never_throw(string text)
    {
        _history.Add("p", null, "SELECT 1", 0, null, "done", null);
        _history.Search(new HistorySearchParams { Text = text });
    }

    // ------------------------------------------------------------------ L17: previews and retention

    [Fact]
    public void List_and_search_send_a_preview_and_get_sends_the_whole_text()
    {
        // Before, the history dialog received up to 300 whole texts (each up to the run's size) in one reply.
        var tail = " AND needle_after_the_preview = 1";
        var sql = "SELECT * FROM t WHERE " + string.Join(" AND ", Enumerable.Range(0, 200).Select(i => $"c{i} = {i}")) + tail;
        var id = _history.Add("p", "db", sql, 1, null, "done", null);
        _history.Add("p", "db", "SELECT 1", 1, null, "done", null);
        var listed = _history.List(new HistoryListParams()).Single(h => h.Id == id);
        Assert.Equal(sql[..HistoryLimits.PreviewChars], listed.Sql);
        Assert.True(listed.SqlTruncated);
        Assert.Equal(sql.Length, listed.SqlLength);
        // The index holds the whole text: a word past the preview still finds the entry.
        var found = Assert.Single(_history.Search(new HistorySearchParams { Text = "needle_after_the_preview" }));
        Assert.Equal((id, true), (found.Id, found.SqlTruncated));
        var full = _history.Get(id);
        Assert.Equal((sql, false, (long)sql.Length), (full.Sql, full.SqlTruncated, full.SqlLength));
        var shortOne = _history.List(new HistoryListParams()).Single(h => h.Sql == "SELECT 1");
        Assert.False(shortOne.SqlTruncated);
        Assert.Equal("historyGone", Assert.Throws<QuerithmException>(() => _history.Get(999_999)).Code);
    }

    [Fact]
    public void A_preview_never_ends_inside_a_surrogate_pair()
    {
        var sql = new string('x', HistoryLimits.PreviewChars - 1) + "\U0001F600 tail";
        var id = _history.Add(null, null, sql, 0, null, "done", null);
        var preview = _history.List(new HistoryListParams()).Single(h => h.Id == id).Sql;
        Assert.Equal(HistoryLimits.PreviewChars - 1, preview.Length);
        Assert.False(char.IsHighSurrogate(preview[^1]));
    }

    [Fact]
    public void Retention_deletes_entries_past_the_age_limit_and_the_oldest_past_the_count_limit_and_their_index_rows()
    {
        var now = 1_800_000_000_000L;
        var clock = now - 100 * 86_400_000L; // 100 days ago
        _history.Now = () => clock;
        var old = _history.Add(null, null, "SELECT aged_out_marker", 0, null, "done", null);
        clock = now - 10 * 86_400_000L;
        var kept = Enumerable.Range(0, 1_100).Select(i => _history.Add(null, null, $"SELECT {i} AS recent_marker", 0, null, "done", null)).ToArray();
        _history.Now = () => now;
        _history.SetRetention(new HistoryRetention { Days = 90, MaxEntries = 1_000 });
        // SetRetention starts a pass of its own; this one runs after it (or is the same).
        _history.SchedulePrune().Wait(TimeSpan.FromSeconds(30));
        Assert.Equal(0, _history.Prune());
        var left = _history.List(new HistoryListParams { Limit = 2000 }).Select(h => h.Id).ToArray();
        Assert.Equal(1_000, left.Length);
        Assert.DoesNotContain(old, left);
        Assert.Equal(kept[^1_000..].Reverse(), left);
        Assert.Empty(_history.Search(new HistorySearchParams { Text = "aged_out_marker" }));
        Assert.Equal(1_000, _history.Search(new HistorySearchParams { Text = "recent_marker", Limit = 2000 }).Length);
    }

    [Fact]
    public void The_retention_setting_is_validated_stored_and_read_back_and_defaults_to_90_days_and_50000_entries()
    {
        Assert.Equal(new HistoryRetention { Days = 90, MaxEntries = 50_000 }, _history.Retention());
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => _history.SetRetention(new HistoryRetention { Days = 0, MaxEntries = 5_000 })).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => _history.SetRetention(new HistoryRetention { Days = 30, MaxEntries = 10 })).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => _history.SetRetention(new HistoryRetention { Days = 30, MaxEntries = 2_000_000 })).Code);
        _history.SetRetention(new HistoryRetention { Days = 30, MaxEntries = 5_000 });
        Assert.Equal(new HistoryRetention { Days = 30, MaxEntries = 5_000 }, _history.Retention());
    }

    [Fact]
    public void A_stored_retention_that_is_unreadable_or_out_of_range_reads_as_the_default()
    {
        var store = LocalStore.InMemory();
        var history = new HistoryStore(store);
        var settings = new SettingsStore(store);
        settings.Set("history.retention", "{\"days\": 0, \"maxEntries\": 5}");
        Assert.Equal(new HistoryRetention(), history.Retention());
        settings.Set("history.retention", "\"not an object\"");
        Assert.Equal(new HistoryRetention(), history.Retention());
    }

    [Fact]
    public async Task The_first_retention_pass_that_deletes_entries_says_so_once_naming_the_setting()
    {
        var store = LocalStore.InMemory();
        var history = new HistoryStore(store);
        var notices = new List<SystemNotice>();
        history.Notice += n => { lock (notices) notices.Add(n); };
        var now = 1_800_000_000_000L;
        history.Now = () => now;
        history.Add(null, null, "SELECT 'recent'", 0, null, "done", null);
        await history.SchedulePrune();
        Assert.Empty(notices); // nothing deleted, nothing said

        // The first start after an upgrade: entries past 90 days go — once said, with the setting's name.
        history.Now = () => now - 120 * 86_400_000L;
        for (var i = 0; i < 3; i++) history.Add(null, null, $"SELECT {i}", 0, null, "done", null);
        history.Now = () => now;
        await history.SchedulePrune();
        var n = Assert.Single(notices);
        Assert.Equal("historyPruned", n.Code);
        Assert.Contains("deleted 3 query history entries", n.Message);
        Assert.Contains("history retention setting", n.Message);
        Assert.Contains("90 days", n.Message);

        // Later passes, and the next start on the same store, say nothing more.
        history.Now = () => now - 120 * 86_400_000L;
        history.Add(null, null, "SELECT 'old again'", 0, null, "done", null);
        history.Now = () => now;
        await history.SchedulePrune();
        var next = new HistoryStore(store) { Now = () => now };
        next.Notice += n2 => { lock (notices) notices.Add(n2); };
        next.Now = () => now - 120 * 86_400_000L;
        next.Add(null, null, "SELECT 'old once more'", 0, null, "done", null);
        next.Now = () => now;
        await next.SchedulePrune();
        Assert.Single(notices);
        Assert.Equal("SELECT 'recent'", Assert.Single(history.List(new HistoryListParams())).Sql);
    }

    [Fact]
    public async Task Every_500_runs_start_a_background_pass_off_the_caller()
    {
        _history.SetRetention(new HistoryRetention { Days = 1, MaxEntries = 1_000 });
        await _history.SchedulePrune();
        var now = 1_800_000_000_000L;
        _history.Now = () => now - 2 * 86_400_000L;
        for (var i = 0; i < HistoryLimits.PruneEvery - 1; i++) _history.Add(null, null, $"SELECT {i}", 0, null, "done", null);
        _history.Now = () => now;
        Assert.Equal(HistoryLimits.PruneEvery - 1, _history.List(new HistoryListParams { Limit = 2000 }).Length);
        _history.Add(null, null, "SELECT 'the 500th'", 0, null, "done", null); // starts the pass
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_history.List(new HistoryListParams { Limit = 2000 }).Length > 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "no retention pass ran after 500 runs");
            await Task.Delay(20);
        }
        Assert.Equal("SELECT 'the 500th'", Assert.Single(_history.List(new HistoryListParams())).Sql);
    }
}

/// <summary>profile.save validation and profile groups, kept in settings.</summary>
public class ProfileRpcTests
{
    [Theory]
    [InlineData("once")]
    [InlineData("session")]
    [InlineData("duration")]
    public async Task Temporary_password_must_be_supplied_for_each_new_session(string policy)
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "temporary", Engine = Engine.Mssql, Auth = AuthKind.Sql, PasswordRetention = policy, PasswordRetentionMinutes = 15 });
        var first = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "cached" });
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id }));
        Assert.Equal("needsSecret", ex.Code);
        // Providing a fresh credential allows another session; opening is lazy and does not dial.
        var second = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "fresh" });
        Assert.NotEqual(first.SessionId, second.SessionId);
        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = first.SessionId, Force = true });
        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = second.SessionId, Force = true });
        Assert.Null(h.Services.Sessions.SecretFor(p.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Saving_a_profile_without_a_name_is_invalid(string name)
    {
        await using var h = new TestHost();
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.ProfileSave(new Profile { Id = "", Name = name, Engine = Engine.Mssql }));
        Assert.Equal("invalid", ex.Code);
        Assert.Empty(await h.Rpc.ProfileList());
    }

    [Fact]
    public async Task Groups_replace_by_name_ignoring_case_and_stay_sorted()
    {
        await using var h = new TestHost();
        Assert.Empty(await h.Rpc.GroupList());
        await h.Rpc.GroupSave(new ProfileGroup { Name = "Zeta" });
        await h.Rpc.GroupSave(new ProfileGroup { Name = "alpha", Color = "red" });
        var list = await h.Rpc.GroupSave(new ProfileGroup { Name = "ALPHA", Color = "blue" });
        Assert.Equal(["ALPHA", "Zeta"], list.Select(g => g.Name));
        Assert.Equal("blue", list[0].Color);
        Assert.Equal(list, await h.Rpc.GroupList());
    }

    [Fact]
    public async Task Deleting_a_group_ignores_case_and_unknown_names()
    {
        await using var h = new TestHost();
        await h.Rpc.GroupSave(new ProfileGroup { Name = "Ops" });
        Assert.Single(await h.Rpc.GroupDelete(new ProfileGroup { Name = "unknown" }));
        Assert.Empty(await h.Rpc.GroupDelete(new ProfileGroup { Name = "ops" }));
    }

    [Fact]
    public async Task Deleted_profile_is_gone_from_the_list()
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "x", Engine = Engine.Pg });
        await h.Rpc.ProfileDelete(new ProfileRef { ProfileId = p.Id });
        Assert.Empty(await h.Rpc.ProfileList());
    }

    [Theory]
    [InlineData(Engine.Mssql)]
    [InlineData(Engine.Pg)]
    public async Task Deleting_a_profile_removes_its_history_metadata_and_cached_snapshots(Engine engine)
    {
        await using var h = new TestHost();
        var gone = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "gone", Engine = engine });
        var kept = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "kept", Engine = engine });
        foreach (var p in new[] { gone, kept })
        {
            h.Services.History.Add(p.Id, "db", "SELECT marker_text FROM t", 1, 1, "done", null);
            using var c = h.Services.Store.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO schema_snapshot (profile_id, database, status) VALUES ($p, 'db', 'fresh');
                INSERT INTO obj (profile_id, database, object_id, schema_name, name, kind) VALUES ($p, 'db', 1, 's', 't', 'table');
                INSERT INTO definition_cache (profile_id, database, object_id, definition) VALUES ($p, 'db', 1, 'x');
                INSERT INTO script (id, profile_id) VALUES ($p || '-script', $p);
                """;
            cmd.Parameters.AddWithValue("$p", p.Id);
            cmd.ExecuteNonQuery();
            Assert.Single(h.Services.Metadata.Cache.Get(p.Id, "db")!.Objects);
        }

        await h.Rpc.ProfileDelete(new ProfileRef { ProfileId = gone.Id });

        Assert.Equal([kept.Id], (await h.Rpc.ProfileList()).Select(p => p.Id));
        Assert.Empty(await h.Rpc.HistoryList(new HistoryListParams { ProfileId = gone.Id }));
        Assert.Equal([kept.Id], (await h.Rpc.HistorySearch(new HistorySearchParams { Text = "marker_text" })).Select(e => e.ProfileId));
        Assert.Empty(h.Services.Metadata.Cache.Get(gone.Id, "db")!.Objects);
        Assert.Single(h.Services.Metadata.Cache.Get(kept.Id, "db")!.Objects);
        using var c2 = h.Services.Store.Open();
        foreach (var table in new[] { "schema_snapshot", "obj", "definition_cache", "history", "script" })
        {
            using var count = c2.CreateCommand();
            count.CommandText = $"SELECT group_concat(DISTINCT profile_id) FROM {table}";
            Assert.Equal(kept.Id, count.ExecuteScalar());
        }
    }

    [Fact]
    public async Task Ping_reports_the_contract_version()
    {
        await using var h = new TestHost();
        var r = await h.Rpc.Ping();
        Assert.Equal(ContractInfo.Version, r.Version);
        Assert.Equal(Environment.ProcessId, r.Pid);
    }
}
