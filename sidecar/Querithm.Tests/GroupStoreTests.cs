using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// Connection groups (§6): a group is a stored row or a name profiles carry; names match ignoring case, spacing and
/// Unicode form; every write is one transaction and never touches a profile's environment, color or updated_at.
/// </summary>
public sealed class GroupStoreTests : IDisposable
{
    readonly LocalStore _store = LocalStore.InMemory();
    readonly ProfileStore _profiles;
    readonly GroupStore _groups;
    readonly string _dir = Directory.CreateTempSubdirectory("qgroups-").FullName;

    public GroupStoreTests()
    {
        _profiles = new ProfileStore(_store);
        _groups = new GroupStore(_store, _profiles);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Every engine, active ones included (Oracle's provider is not needed: the store does not validate engines).</summary>
    public static TheoryData<Engine> EveryEngine => new() { Engine.Mssql, Engine.Pg, Engine.MySql, Engine.Oracle, Engine.Sqlite, Engine.DuckDb };

    Profile Save(string name, string? group, Engine engine = Engine.Pg, EnvironmentKind env = EnvironmentKind.Dev) =>
        _profiles.Save(new Profile { Id = "", Name = name, Engine = engine, Environment = env, GroupName = group, Color = "#123456" });

    static QuerithmException Refused(Action act) => Assert.Throws<QuerithmException>(act);

    // ------------------------------------------------------------------ R3: a typed name is a group

    [Fact]
    public void A_typed_name_is_a_group_and_case_and_spacing_variants_are_the_same_group()
    {
        var a = Save("a", "Payments");
        var b = Save("b", "  payments ");
        var c = Save("c", "PAYMENTS");
        Assert.Equal(["Payments", "Payments", "Payments"], new[] { a, b, c }.Select(p => p.GroupName));
        var group = Assert.Single(_groups.List());
        Assert.Equal(("Payments", 3), (group.Name, group.Members));
        Assert.All(_profiles.List(), p => Assert.Equal("Payments", p.GroupName));
    }

    [Fact]
    public void Spaces_inside_a_name_collapse_and_nfc_and_nfd_spellings_are_one_group()
    {
        Save("a", "Core   Banking");
        Save("b", "ড় টিম");          // Bengali RRA, precomposed
        Save("c", "ড় টিম");    // the same letter, decomposed
        var groups = _groups.List();
        Assert.Equal(2, groups.Length);
        Assert.Contains(groups, g => g.Name == "Core Banking");
        Assert.Equal(2, Assert.Single(groups, g => g.Name != "Core Banking").Members);
    }

    [Fact]
    public void A_group_known_only_from_profiles_disappears_with_its_last_member_and_a_stored_one_stays()
    {
        var a = Save("a", "Paymnts");
        _groups.Create("Empty");
        Assert.Equal(["Empty", "Paymnts"], _groups.List().Select(g => g.Name));
        _profiles.Save(a with { GroupName = null });
        Assert.Equal(("Empty", 0), (Assert.Single(_groups.List()).Name, _groups.List()[0].Members));
    }

    [Fact]
    public void Saving_a_profile_takes_the_stored_spelling_and_a_blank_group_is_no_group()
    {
        _groups.Create("Payments");
        Assert.Equal("Payments", Save("a", " payments  ").GroupName);
        Assert.Null(Save("b", "   ").GroupName);
        Assert.Equal("Payments", Scalar("SELECT group_name FROM profile WHERE name = 'a'"));
        Assert.Throws<ArgumentException>(() => Save("c", "a​b"));
    }

    [Fact]
    public void A_profile_alone_in_its_group_can_change_the_groups_spelling()
    {
        var a = Save("a", "payments");
        Assert.Equal("Payments", _profiles.Save(a with { GroupName = "Payments" }).GroupName);
        Assert.Equal("Payments", Assert.Single(_groups.List()).Name);
    }

    [Fact]
    public void Names_that_break_the_rules_stored_by_an_older_build_read_as_no_group()
    {
        var a = Save("a", "Ops");
        Exec($"UPDATE profile SET group_name = 'x' || char(8238) || 'y' WHERE id = '{a.Id}'");
        Assert.Null(_profiles.List().Single().GroupName);
        Assert.Empty(_groups.List());
    }

    // ------------------------------------------------------------------ R4: create, rename, delete

    [Fact]
    public void Create_refuses_a_name_that_exists_in_any_spelling_and_stores_an_empty_group()
    {
        Save("a", "Payments");
        Assert.Equal("groupExists", Refused(() => _groups.Create("PAYMENTS ")).Code);
        _groups.Create("Billing");
        Assert.Equal("groupExists", Refused(() => _groups.Create("billing")).Code);
        Assert.Equal("invalidGroup", Refused(() => _groups.Create("  ")).Code);
        Assert.Equal(0, _groups.List().Single(g => g.Name == "Billing").Members);
    }

    [Fact]
    public void Create_stops_at_the_stored_limit()
    {
        using (var c = _store.Open())
        using (var tx = c.BeginTransaction())
        {
            for (var i = 0; i < GroupLimits.MaxStored; i++)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT INTO profile_group VALUES ('G{i}', 'G{i}', 0, 0)";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Assert.Equal("groupLimit", Refused(() => _groups.Create("one more")).Code);
        // A group made by naming it on a profile is not a stored row and is never capped.
        Assert.Equal("one more", Save("a", "one more").GroupName);
    }

    [Theory]
    [MemberData(nameof(EveryEngine))]
    public void Rename_moves_every_member_and_changes_nothing_else(Engine engine)
    {
        var prod = Save("prod", "Payments", engine, EnvironmentKind.Prod);
        var variant = Save("variant", "Payments", engine);
        Exec($"UPDATE profile SET group_name = 'payments ' WHERE id = '{variant.Id}'"); // an older build's spelling
        var other = Save("other", "Analytics", engine);
        var before = _profiles.Get(prod.Id)!;
        var changed = new List<string>();
        _profiles.Changed += changed.Add;

        var result = _groups.Rename("PAYMENTS", "Billing");

        Assert.Equal(("Payments", "Billing"), (result.Renamed!.From, result.Renamed.To));
        Assert.Equal(new[] { prod.Id, variant.Id }.Order(), result.Changed.Order());
        Assert.Equal(result.Changed.Order(), changed.Order());
        var after = _profiles.Get(prod.Id)!;
        Assert.Equal("Billing", after.GroupName);
        Assert.Equal("Billing", _profiles.Get(variant.Id)!.GroupName);
        Assert.Equal("Analytics", _profiles.Get(other.Id)!.GroupName);
        // The environment, color and updated_at stay (the editor ranks recent engines by updated_at).
        Assert.Equal((EnvironmentKind.Prod, "#123456", before.UpdatedAt), (after.Environment, after.Color, after.UpdatedAt));
        Assert.Equal(["Analytics", "Billing"], _groups.List().Select(g => g.Name));
    }

    [Fact]
    public void Rename_onto_another_group_is_refused_and_a_case_only_rename_is_allowed()
    {
        Save("a", "Payments");
        Save("b", "Billing");
        Assert.Equal("groupExists", Refused(() => _groups.Rename("Payments", "billing")).Code);
        Assert.Equal("Payments", _profiles.List().Single(p => p.Name == "a").GroupName);
        _groups.Rename("Payments", "PAYMENTS");
        Assert.Equal("PAYMENTS", _profiles.List().Single(p => p.Name == "a").GroupName);
        Assert.Equal("notFound", Refused(() => _groups.Rename("nothing", "x")).Code);
    }

    [Fact]
    public void A_case_only_rename_reports_every_member_also_one_already_stored_in_the_new_spelling()
    {
        // Regression: the reply named only the profiles it rewrote, so one an older build stored as "Payments" kept showing
        // under "payments" (profile.list shows the group's spelling) — a second, stale group in the UI until a reload.
        _groups.Create("payments");
        var a = Save("a", "payments");
        var b = Save("b", "payments");
        Exec($"UPDATE profile SET group_name = 'Payments' WHERE id = '{b.Id}'");
        Assert.All(_profiles.List(), p => Assert.Equal("payments", p.GroupName));

        var result = _groups.Rename("payments", "Payments");

        Assert.Equal(new[] { a.Id, b.Id }.Order(), result.Changed.Order());
        Assert.All(_profiles.List(), p => Assert.Equal("Payments", p.GroupName));
        Assert.Equal(["Payments"], _groups.List().Select(g => g.Name));
    }

    [Fact]
    public void A_rename_retried_after_it_committed_succeeds_without_changing_anything()
    {
        Save("a", "Payments");
        _groups.Rename("Payments", "Billing");
        var retry = _groups.Rename("Payments", "Billing");
        Assert.Empty(retry.Changed);
        Assert.Equal("Billing", retry.Renamed!.To);
    }

    [Fact]
    public void Delete_ungroups_its_members_and_never_deletes_a_profile()
    {
        var a = Save("a", "Payments", env: EnvironmentKind.Prod);
        Save("b", "payments");
        _groups.Create("Empty");
        var result = _groups.Delete("PAYMENTS", expectedMembers: 2);
        Assert.Equal(2, result.Changed.Length);
        Assert.Equal(2, _profiles.List().Length);
        Assert.All(_profiles.List(), p => Assert.Null(p.GroupName));
        Assert.Equal(EnvironmentKind.Prod, _profiles.Get(a.Id)!.Environment);
        Assert.Equal(["Empty"], _groups.List().Select(g => g.Name));
        // Unknown names, and names that could not be a group, are retry-safe no-ops.
        Assert.Empty(_groups.Delete("Payments", null).Changed);
        Assert.Empty(_groups.Delete("a​b", null).Changed);
    }

    [Fact]
    public void Delete_with_a_stale_member_count_is_refused_and_changes_nothing()
    {
        Save("a", "Payments");
        Save("b", "Payments");
        var ex = Refused(() => _groups.Delete("Payments", expectedMembers: 1));
        Assert.Equal("groupChanged", ex.Code);
        Assert.Equal(2, ((Dictionary<string, int>)ex.DataPayload!)["members"]);
        Assert.All(_profiles.List(), p => Assert.Equal("Payments", p.GroupName));
        Assert.Equal("invalid", Refused(() => _groups.Delete("Payments", -1)).Code);
    }

    // ------------------------------------------------------------------ bulk move

    [Theory]
    [MemberData(nameof(EveryEngine))]
    public void Assign_moves_profiles_into_a_group_a_new_name_creates_it_and_null_ungroups(Engine engine)
    {
        var a = Save("a", null, engine, EnvironmentKind.Prod);
        var b = Save("b", "Old", engine);
        var c = Save("c", null, engine);
        var before = _profiles.Get(a.Id)!;

        var moved = _groups.Assign([a.Id, b.Id, a.Id], "Core   Banking");
        Assert.Equal(new[] { a.Id, b.Id }.Order(), moved.Changed.Order());
        Assert.Null(moved.Missing);
        Assert.Equal("Core Banking", _profiles.Get(a.Id)!.GroupName);
        Assert.Equal(["Core Banking"], _groups.List().Select(g => g.Name));
        var after = _profiles.Get(a.Id)!;
        Assert.Equal((EnvironmentKind.Prod, before.UpdatedAt, before.Color), (after.Environment, after.UpdatedAt, after.Color));
        Assert.Null(_profiles.Get(c.Id)!.GroupName);

        // A retry changes nothing; null moves them out.
        Assert.Empty(_groups.Assign([a.Id, b.Id], "core banking").Changed);
        Assert.Equal(2, _groups.Assign([a.Id, b.Id], null).Changed.Length);
        Assert.Empty(_groups.List());
    }

    [Fact]
    public void Assign_skips_profiles_deleted_meanwhile_and_moves_the_rest()
    {
        var a = Save("a", null);
        var b = Save("b", null);
        _profiles.Delete(b.Id);
        var result = _groups.Assign([a.Id, b.Id], "Payments");
        Assert.Equal([a.Id], result.Changed);
        Assert.Equal([b.Id], result.Missing!);
    }

    [Fact]
    public void Assign_joins_an_existing_group_in_its_stored_spelling()
    {
        _groups.Create("Payments");
        var a = Save("a", null);
        _groups.Assign([a.Id], "PAYMENTS");
        Assert.Equal("Payments", _profiles.Get(a.Id)!.GroupName);
    }

    [Fact]
    public void Assign_refuses_bad_ids_and_names_before_writing()
    {
        var a = Save("a", "Keep");
        Assert.Equal("invalidGroup", Refused(() => _groups.Assign([], "x")).Code);
        Assert.Equal("invalidGroup", Refused(() => _groups.Assign(["../x"], "x")).Code);
        Assert.Equal("invalidGroup", Refused(() => _groups.Assign(new string[GroupLimits.MaxAssign + 1], "x")).Code);
        Assert.Equal("invalidGroup", Refused(() => _groups.Assign([a.Id], "x‮y")).Code);
        Assert.Equal("Keep", _profiles.Get(a.Id)!.GroupName);
    }

    // ------------------------------------------------------------------ failure recovery

    [Fact]
    public void A_fault_in_the_middle_of_a_rename_leaves_every_profile_and_the_group_as_they_were()
    {
        Save("a", "Payments");
        var b = Save("b", "Payments");
        _groups.Create("Other");
        Exec($"CREATE TRIGGER fault BEFORE UPDATE OF group_name ON profile WHEN NEW.id = '{b.Id}' BEGIN SELECT RAISE(ABORT, 'fault'); END;");
        Assert.Throws<SqliteException>(() => _groups.Rename("Payments", "Billing"));
        Assert.All(_profiles.List(), p => Assert.Equal("Payments", p.GroupName));
        Assert.Equal(["Other", "Payments"], _groups.List().Select(g => g.Name));
        Assert.Throws<SqliteException>(() => _groups.Assign([_profiles.List()[0].Id, b.Id], "Other"));
        Assert.Throws<SqliteException>(() => _groups.Delete("Payments", null));
        Assert.All(_profiles.List(), p => Assert.Equal("Payments", p.GroupName));
    }

    [Fact]
    public async Task Two_instances_writing_at_once_lose_no_write_and_never_store_one_name_twice()
    {
        var path = Path.Combine(_dir, "two.db");
        var storeA = new LocalStore(path, busyTimeout: TimeSpan.FromSeconds(20));
        var storeB = new LocalStore(path, busyTimeout: TimeSpan.FromSeconds(20));
        var a = new GroupStore(storeA, new ProfileStore(storeA));
        var b = new GroupStore(storeB, new ProfileStore(storeB));
        using (var barrier = new Barrier(2))
        {
            await Task.WhenAll(
                Task.Run(() => { barrier.SignalAndWait(); for (var i = 0; i < 20; i++) a.Create($"A{i}"); }),
                Task.Run(() => { barrier.SignalAndWait(); for (var i = 0; i < 20; i++) b.Create($"B{i}"); }));
        }
        Assert.Equal(40, a.List().Length);
        // The same name from both at once: one stores it, the other is told it exists (never a constraint error).
        using var race = new Barrier(2);
        var outcomes = await Task.WhenAll(
            Task.Run(() => Outcome(() => { race.SignalAndWait(); a.Create("Same"); })),
            Task.Run(() => Outcome(() => { race.SignalAndWait(); b.Create("same"); })));
        Assert.Equal(["groupExists", "ok"], outcomes.Order());
        Assert.Equal(41, b.List().Length);
    }

    static string Outcome(Action act)
    {
        try { act(); return "ok"; }
        catch (QuerithmException ex) { return ex.Code; }
    }

    [Fact]
    public async Task A_store_another_instance_holds_answers_store_busy_and_works_again_once_it_is_free()
    {
        var path = Path.Combine(_dir, "busy.db");
        var store = new LocalStore(path, busyTimeout: TimeSpan.FromSeconds(1));
        var services = Services.Create(store, new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance,
            new ProviderRegistry(new IDbProvider[] { new SqlServerProvider(), new PostgresProvider() }), _dir);
        await using var _ = services;
        var rpc = new RpcServer(services, () => { });
        using (var holder = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            holder.Open();
            using (var begin = holder.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE;";
                begin.ExecuteNonQuery();
            }
            var ex = await Assert.ThrowsAsync<QuerithmException>(() => rpc.GroupCreate(new GroupCreateParams { Name = "Payments" }));
            Assert.Equal("storeBusy", ex.Code);
            Assert.Empty(await rpc.GroupList()); // reads still work under the writer (WAL)
        }
        Assert.Equal("Payments", Assert.Single(await rpc.GroupCreate(new GroupCreateParams { Name = "Payments" })).Name);
    }

    // ------------------------------------------------------------------ RPC boundary

    [Fact]
    public async Task Profile_save_refuses_a_bad_group_name_at_the_field_and_canonicalizes_a_good_one()
    {
        await using var h = new TestHost();
        var p = new Profile { Id = "", Name = "srv", Engine = Engine.Pg, Host = "127.0.0.1", Port = 1, Auth = AuthKind.Password, Username = "u" };
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.ProfileSave(p with { GroupName = "a​b" }));
        Assert.Equal("invalidProfile", ex.Code);
        Assert.Equal("groupName", ((Dictionary<string, string>)ex.DataPayload!)["field"]);
        Assert.Empty(await h.Rpc.ProfileList());
        await h.Rpc.GroupCreate(new GroupCreateParams { Name = "Payments" });
        Assert.Equal("Payments", (await h.Rpc.ProfileSave(p with { GroupName = "  PAYMENTS " })).GroupName);
        Assert.Equal(1, Assert.Single(await h.Rpc.GroupList()).Members);
    }

    [Fact]
    public async Task Group_replies_carry_the_changed_profiles_as_profile_list_shows_them()
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "srv", Engine = Engine.Pg, Host = "127.0.0.1", Port = 1, Auth = AuthKind.Password, Username = "u" });
        var moved = await h.Rpc.GroupAssign(new GroupAssignParams { ProfileIds = [p.Id], Name = "Payments" });
        Assert.Equal("Payments", Assert.Single(moved.Profiles).GroupName);
        Assert.Equal(1, Assert.Single(moved.Groups).Members);
        var renamed = await h.Rpc.GroupRename(new GroupRenameParams { From = "payments", To = "Billing" });
        Assert.Equal("Billing", Assert.Single(renamed.Profiles).GroupName);
        Assert.Equal(("Payments", "Billing"), (renamed.Renamed!.From, renamed.Renamed.To));
        var deleted = await h.Rpc.GroupDelete(new GroupDeleteParams { Name = "Billing", ExpectedMembers = 1 });
        Assert.Null(Assert.Single(deleted.Profiles).GroupName);
        Assert.Empty(deleted.Groups);
    }

    // ------------------------------------------------------------------ the shared fixture (src/connections/groupNames.ts reads it too)

    static string FixturePath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "package.json")) && Directory.Exists(Path.Combine(d.FullName, "src", "connections")))
                return Path.Combine(d.FullName, "src", "connections", "__tests__", "fixtures", "group-names.json");
        throw new DirectoryNotFoundException("repository root");
    }

    sealed record NameCase(string Raw, string? Name, string? Key, bool Error);

    [Fact]
    public void Names_clean_and_key_exactly_as_the_editor_hint_does()
    {
        var cases = System.Text.Json.JsonSerializer.Deserialize<NameCase[]>(File.ReadAllText(FixturePath()), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.True(cases.Length >= 20);
        Assert.All(cases, c =>
        {
            var problem = GroupNames.Clean(c.Raw, out var name);
            Assert.True((problem != null) == c.Error, $"{c.Raw}: refused {problem != null}, fixture says {c.Error}");
            Assert.Equal(c.Name, name);
            if (name != null) Assert.Equal(c.Key, GroupNames.Key(name));
        });
    }

    object? Scalar(string sql)
    {
        using var c = _store.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    void Exec(string sql)
    {
        using var c = _store.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
