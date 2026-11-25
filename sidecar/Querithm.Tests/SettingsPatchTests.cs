using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// settings.patch (§16, 2026-11-25): the webview saves only the members of <c>ui.settings</c> it changed, merged under the
/// store's write lock, so two Querithm instances saving at once each keep what the other wrote.
/// </summary>
public sealed class SettingsPatchTests : IDisposable
{
    const string Key = "ui.settings";
    readonly LocalStore _store = LocalStore.InMemory();
    readonly SettingsStore _settings;
    readonly string _dir = Directory.CreateTempSubdirectory("qsettings-").FullName;

    public SettingsPatchTests() => _settings = new SettingsStore(_store);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    static Dictionary<string, string> Json(params (string Name, string Json)[] members) => members.ToDictionary(m => m.Name, m => m.Json);

    static JsonObject Read(SettingsStore s) => JsonNode.Parse(Assert.Single(s.Get([Key])).ValueJson)!.AsObject();

    [Fact]
    public void A_first_patch_makes_the_setting_with_the_members_for_a_new_one()
    {
        _settings.Patch(Key, Json(("editorFontSize", "15")), Json(("syntaxColors", "\"default\""), ("syntaxTheme", "\"standard\"")), null);
        var o = Read(_settings);
        Assert.Equal(["editorFontSize", "syntaxColors", "syntaxTheme"], o.Select(m => m.Key).Order());
        Assert.Equal(15, (int)o["editorFontSize"]!);
        Assert.Equal("default", (string)o["syntaxColors"]!);
    }

    [Fact]
    public void Members_already_stored_keep_their_value_and_place_and_set_when_new_is_ignored()
    {
        _settings.Set(Key, """{"theme":"light","darkTheme":"a-theme-from-2027","wordWrap":true}""");
        _settings.Patch(Key, Json(("wordWrap", "false"), ("minimap", "false")), Json(("syntaxColors", "\"default\"")), null);
        var o = Read(_settings);
        Assert.Equal(["theme", "darkTheme", "wordWrap", "minimap"], o.Select(m => m.Key));
        Assert.Equal("a-theme-from-2027", (string)o["darkTheme"]!); // another version's value, kept as it was
        Assert.False((bool)o["wordWrap"]!);
        Assert.Null(o["syntaxColors"]);
    }

    [Fact]
    public void Set_wins_over_set_when_new_and_remove_drops_members()
    {
        _settings.Patch(Key, Json(("syntaxColors", "\"neon\"")), Json(("syntaxColors", "\"default\"")), null);
        Assert.Equal("neon", (string)Read(_settings)["syntaxColors"]!);
        _settings.Patch(Key, null, null, ["syntaxColors", "absent"]);
        Assert.Empty(Read(_settings));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    public void A_stored_value_that_holds_no_members_counts_as_new(string stored)
    {
        _settings.Set(Key, stored);
        _settings.Patch(Key, Json(("a", "1")), Json(("first", "true")), null);
        var o = Read(_settings);
        Assert.Equal(1, (int)o["a"]!);
        Assert.True((bool)o["first"]!);
    }

    [Fact]
    public void A_repeated_stored_member_keeps_its_last_value_as_the_webview_reads_it()
    {
        _settings.Set(Key, """{"theme":"dark","theme":"light"}""");
        _settings.Patch(Key, Json(("wordWrap", "true")), null, null);
        var o = Read(_settings);
        Assert.Equal("light", (string)o["theme"]!);
        Assert.Equal(2, o.Count);
    }

    [Fact]
    public void A_value_that_is_not_json_is_refused_and_nothing_is_written()
    {
        _settings.Set(Key, """{"theme":"dark"}""");
        var ex = Assert.Throws<QuerithmException>(() => _settings.Patch(Key, Json(("theme", "\"light\""), ("broken", "{nope")), null, null));
        Assert.Equal("invalid", ex.Code);
        Assert.Contains("broken", ex.Message);
        Assert.Equal("""{"theme":"dark"}""", Assert.Single(_settings.Get([Key])).ValueJson);
    }

    [Fact]
    public async Task Two_instances_patching_at_once_keep_every_member_either_wrote()
    {
        var path = Path.Combine(_dir, "two.db");
        var a = new SettingsStore(new LocalStore(path, busyTimeout: TimeSpan.FromSeconds(20)));
        var b = new SettingsStore(new LocalStore(path, busyTimeout: TimeSpan.FromSeconds(20)));
        a.Set(Key, """{"shared":0}""");
        using (var barrier = new Barrier(2))
        {
            await Task.WhenAll(
                Task.Run(() => { barrier.SignalAndWait(); for (var i = 0; i < 40; i++) a.Patch(Key, Json(($"a{i}", $"{i}")), null, null); }),
                Task.Run(() => { barrier.SignalAndWait(); for (var i = 0; i < 40; i++) b.Patch(Key, Json(($"b{i}", $"{i}")), null, null); }));
        }
        var o = Read(a);
        Assert.Equal(81, o.Count);
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal(i, (int)o[$"a{i}"]!);
            Assert.Equal(i, (int)o[$"b{i}"]!);
        }
    }

    [Fact]
    public async Task A_store_another_instance_holds_answers_store_busy_and_changes_nothing()
    {
        var path = Path.Combine(_dir, "busy.db");
        var store = new LocalStore(path, busyTimeout: TimeSpan.FromSeconds(1));
        var services = Services.Create(store, new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance,
            new ProviderRegistry(new IDbProvider[] { new SqlServerProvider(), new PostgresProvider() }), _dir);
        await using var _ = services;
        var rpc = new RpcServer(services, () => { });
        await rpc.SettingsSet(new SettingEntry { Key = Key, ValueJson = """{"theme":"dark"}""" });
        using (var holder = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            holder.Open();
            using (var begin = holder.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE;";
                begin.ExecuteNonQuery();
            }
            var ex = await Assert.ThrowsAsync<QuerithmException>(() => rpc.SettingsPatch(new SettingsPatchParams { Key = Key, Set = Json(("theme", "\"light\"")) }));
            Assert.Equal("storeBusy", ex.Code);
        }
        Assert.Equal("""{"theme":"dark"}""", Assert.Single(await rpc.SettingsGet(new SettingsGetParams { Keys = [Key] })).ValueJson);
        await rpc.SettingsPatch(new SettingsPatchParams { Key = Key, Set = Json(("theme", "\"light\"")) });
        Assert.Equal("""{"theme":"light"}""", Assert.Single(await rpc.SettingsGet(new SettingsGetParams { Keys = [Key] })).ValueJson);
    }

    [Fact]
    public async Task The_rpc_round_trips_through_settings_get()
    {
        await using var h = new TestHost();
        await h.Rpc.SettingsPatch(new SettingsPatchParams { Key = Key, Set = Json(("editorFontSize", "14")), SetWhenNew = Json(("syntaxColors", "\"default\"")) });
        await h.Rpc.SettingsPatch(new SettingsPatchParams { Key = Key, Set = Json(("wordWrap", "true")), Remove = ["prodConfirmations"] });
        var o = JsonNode.Parse(Assert.Single(await h.Rpc.SettingsGet(new SettingsGetParams { Keys = [Key] })).ValueJson)!.AsObject();
        // SetWhenNew is applied before Set (the documented order), so a first save's members come first.
        Assert.Equal(["syntaxColors", "editorFontSize", "wordWrap"], o.Select(m => m.Key));
    }

    public static TheoryData<string, SettingsPatchParams> Refused => new()
    {
        { "empty key", new SettingsPatchParams { Key = "" } },
        { "long key", new SettingsPatchParams { Key = new string('k', SettingsPatchLimits.MaxNameLength + 1) } },
        { "old groups key", new SettingsPatchParams { Key = "profile.groups", Set = Json(("a", "1")) } },
        { "empty member name", new SettingsPatchParams { Key = Key, Set = Json(("", "1")) } },
        { "long member name", new SettingsPatchParams { Key = Key, Remove = [new string('m', SettingsPatchLimits.MaxNameLength + 1)] } },
        { "null member name", new SettingsPatchParams { Key = Key, Remove = [null!] } },
        {
            "too many members",
            new SettingsPatchParams { Key = Key, Set = Enumerable.Range(0, SettingsPatchLimits.MaxMembers).ToDictionary(i => $"m{i}", _ => "1"), Remove = ["one more"] }
        },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task The_rpc_refuses_what_is_out_of_bounds_and_stores_nothing(string why, SettingsPatchParams p)
    {
        await using var h = new TestHost();
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SettingsPatch(p));
        Assert.True(ex.Code == "invalid", why);
        Assert.Empty(await h.Rpc.SettingsGet(new SettingsGetParams { Keys = [Key, "profile.groups", QueryGuard.SettingKey] }));
    }

    [Fact]
    public async Task The_most_members_allowed_are_taken()
    {
        await using var h = new TestHost();
        await h.Rpc.SettingsPatch(new SettingsPatchParams { Key = Key, Set = Enumerable.Range(0, SettingsPatchLimits.MaxMembers).ToDictionary(i => $"m{i}", i => $"{i}") });
        Assert.Equal(SettingsPatchLimits.MaxMembers, JsonNode.Parse(Assert.Single(await h.Rpc.SettingsGet(new SettingsGetParams { Keys = [Key] })).ValueJson)!.AsObject().Count);
    }
}
