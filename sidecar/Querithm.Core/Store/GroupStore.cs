using Microsoft.Data.Sqlite;
using Querithm.Contract;
using Querithm.Core.Sessions;

namespace Querithm.Core.Store;

/// <summary>What a group write changed: the groups after it and the profiles whose group name it rewrote.</summary>
public sealed record GroupChange(ProfileGroup[] Groups, string[] Changed, GroupRenamed? Renamed = null, string[]? Missing = null);

/// <summary>
/// Connection groups (§6). A group is a stored <c>profile_group</c> row (an empty group made with New) or a name at least
/// one profile carries, so a typed name is a group and a typo disappears with its last member. Each write is one
/// IMMEDIATE transaction: a rename, delete or move never leaves some profiles changed and others not. A write never
/// touches a profile's environment, color, secrets or sessions, and never bumps its updated_at (the editor ranks recent
/// engines by it).
/// </summary>
public sealed class GroupStore(LocalStore store, ProfileStore profiles)
{
    public ProfileGroup[] List()
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: true);
        var groups = Groups(Resolve(c, tx));
        tx.Commit();
        return groups;
    }

    public ProfileGroup[] Create(string? raw)
    {
        var name = Required(raw);
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        var map = Resolve(c, tx);
        if (map.ContainsKey(GroupNames.Key(name))) throw Exists();
        using (var count = c.Cmd("SELECT COUNT(*) FROM profile_group", tx))
            if ((long)count.ExecuteScalar()! >= GroupLimits.MaxStored)
                throw new QuerithmException("groupLimit", $"Querithm keeps at most {GroupLimits.MaxStored} groups. Delete one you no longer use first.");
        var now = Time.NowMs();
        c.Cmd("INSERT INTO profile_group (name_key, name, created_at, updated_at) VALUES ($k, $n, $t, $t)", tx,
            ("$k", GroupNames.Key(name)), ("$n", name), ("$t", now)).ExecuteNonQuery();
        var groups = Groups(Resolve(c, tx));
        tx.Commit();
        return groups;
    }

    public GroupChange Rename(string? from, string? to)
    {
        var fromKey = GroupNames.LookupKey(from) ?? throw NotFound();
        var name = Required(to);
        var toKey = GroupNames.Key(name);
        string[] changed;
        GroupRenamed renamed;
        using (var c = store.Open())
        using (var tx = c.BeginTransaction(deferred: false))
        {
            var map = Resolve(c, tx);
            if (!map.TryGetValue(fromKey, out var old))
            {
                // A retry after the reply was lost: the rename already happened.
                if (map.TryGetValue(toKey, out var done) && done.Name == name)
                    return new GroupChange(Groups(map), [], new GroupRenamed { From = from!, To = done.Name });
                throw NotFound();
            }
            if (toKey != fromKey && map.ContainsKey(toKey)) throw Exists();
            long? created;
            using (var cmd = c.Cmd("SELECT created_at FROM profile_group WHERE name_key = $k", tx, ("$k", fromKey)))
                created = cmd.ExecuteScalar() is long t ? t : null;
            var now = Time.NowMs();
            c.Cmd("DELETE FROM profile_group WHERE name_key = $k", tx, ("$k", fromKey)).ExecuteNonQuery();
            // Renaming a group known only from its profiles stores it: the user acted on it on purpose.
            c.Cmd("INSERT INTO profile_group (name_key, name, created_at, updated_at) VALUES ($k, $n, $c, $u)", tx,
                ("$k", toKey), ("$n", name), ("$c", created ?? now), ("$u", now)).ExecuteNonQuery();
            // Every spelling of the old name moves, an older build's variants included. The reply names every member: one
            // whose stored spelling already is the new name was shown under the old one (profile.list shows the group's
            // spelling), so leaving it out kept a second, stale group in the UI until a reload.
            var members = Members(c, tx, fromKey);
            foreach (var m in members.Where(m => m.Raw != name)) SetGroup(c, tx, m.Id, name);
            changed = members.Select(m => m.Id).ToArray();
            tx.Commit();
            renamed = new GroupRenamed { From = old.Name, To = name };
        }
        Notify(changed);
        return new GroupChange(List(), changed, renamed);
    }

    /// <summary>Its profiles become ungrouped (none is deleted). An unknown name changes nothing, so a retry is safe.</summary>
    public GroupChange Delete(string? raw, int? expectedMembers)
    {
        if (expectedMembers is < 0) throw QuerithmException.Invalid("expectedMembers must be 0 or more.");
        if (GroupNames.LookupKey(raw) is not { } key) return new GroupChange(List(), []);
        string[] ids;
        using (var c = store.Open())
        using (var tx = c.BeginTransaction(deferred: false))
        {
            ids = Members(c, tx, key).Select(m => m.Id).ToArray();
            if (expectedMembers is { } expected && expected != ids.Length)
                throw new QuerithmException("groupChanged",
                    $"The group now has {ids.Length} connection{(ids.Length == 1 ? "" : "s")}, not {expected}. Check it and delete it again.",
                    new Dictionary<string, int> { ["members"] = ids.Length });
            c.Cmd("DELETE FROM profile_group WHERE name_key = $k", tx, ("$k", key)).ExecuteNonQuery();
            foreach (var id in ids) SetGroup(c, tx, id, null);
            tx.Commit();
        }
        Notify(ids);
        return new GroupChange(List(), ids);
    }

    /// <summary>
    /// Moves profiles into the group <paramref name="raw"/> names (a new name makes the group) or, with null, out of their
    /// group. Profiles already there are left alone, so a retry changes nothing; ids that name no profile (deleted
    /// meanwhile) come back in <see cref="GroupChange.Missing"/> and the others still move.
    /// </summary>
    public GroupChange Assign(string[]? ids, string? raw)
    {
        if (ids is not { Length: >= 1 and <= GroupLimits.MaxAssign })
            throw new QuerithmException("invalidGroup", $"Pick between 1 and {GroupLimits.MaxAssign} connections to move.", Field("profileIds"));
        if (!ids.All(ProfileRules.ValidId))
            throw new QuerithmException("invalidGroup", "A connection id was not assigned by Querithm.", Field("profileIds"));
        string? name = null;
        if (GroupNames.Clean(raw, out name, "name") is { } bad) throw Invalid(bad);
        var changed = new List<string>();
        var missing = new List<string>();
        using (var c = store.Open())
        using (var tx = c.BeginTransaction(deferred: false))
        {
            var target = name == null ? null : Canonical(c, tx, name);
            var targetKey = target == null ? null : GroupNames.Key(target);
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                using var cmd = c.Cmd("SELECT group_name FROM profile WHERE id = $id", tx, ("$id", id));
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                {
                    missing.Add(id);
                    continue;
                }
                var current = GroupNames.CleanOrNull(r.Str(0)) is { } n ? GroupNames.Key(n) : null;
                r.Close();
                if (current == targetKey) continue;
                SetGroup(c, tx, id, target);
                changed.Add(id);
            }
            tx.Commit();
        }
        Notify(changed);
        return new GroupChange(List(), changed.ToArray(), null, missing.Count > 0 ? missing.ToArray() : null);
    }

    /// <summary>
    /// The spelling a cleaned name takes when stored: the group's row, else the spelling most other profiles carry, else
    /// the name as cleaned. <paramref name="excludeId"/> leaves the profile being saved out of the count.
    /// </summary>
    internal static string Canonical(SqliteConnection c, SqliteTransaction tx, string cleaned, string? excludeId = null) =>
        GroupNames.Resolve(RowNames(c, tx), ProfileNames(c, tx, excludeId), out _).TryGetValue(GroupNames.Key(cleaned), out var e) ? e.Name : cleaned;

    internal static List<string> RowNames(SqliteConnection c, SqliteTransaction tx)
    {
        using var cmd = c.Cmd("SELECT name FROM profile_group", tx);
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) if (r.Str(0) is { } n) names.Add(n);
        return names;
    }

    static List<(string, int)> ProfileNames(SqliteConnection c, SqliteTransaction tx, string? excludeId)
    {
        using var cmd = c.Cmd("SELECT group_name, COUNT(*) FROM profile WHERE group_name IS NOT NULL AND ($ex IS NULL OR id <> $ex) GROUP BY group_name", tx,
            ("$ex", excludeId));
        using var r = cmd.ExecuteReader();
        var names = new List<(string, int)>();
        while (r.Read()) names.Add((r.GetString(0), r.GetInt32(1)));
        return names;
    }

    static Dictionary<string, GroupEntry> Resolve(SqliteConnection c, SqliteTransaction tx) => GroupNames.Resolve(RowNames(c, tx), ProfileNames(c, tx, null), out _);

    static ProfileGroup[] Groups(Dictionary<string, GroupEntry> map) =>
        map.Values.Select(e => new ProfileGroup { Name = e.Name, Members = e.Members })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Name, StringComparer.Ordinal).ToArray();

    static List<(string Id, string Raw)> Members(SqliteConnection c, SqliteTransaction tx, string key)
    {
        using var cmd = c.Cmd("SELECT id, group_name FROM profile WHERE group_name IS NOT NULL", tx);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read())
            if (GroupNames.CleanOrNull(r.GetString(1)) is { } n && GroupNames.Key(n) == key) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    static void SetGroup(SqliteConnection c, SqliteTransaction tx, string id, string? name) =>
        c.Cmd("UPDATE profile SET group_name = $g WHERE id = $id", tx, ("$g", name), ("$id", id)).ExecuteNonQuery();

    void Notify(IEnumerable<string> ids)
    {
        foreach (var id in ids) profiles.NotifyChanged(id);
    }

    static string Required(string? raw)
    {
        if (GroupNames.Clean(raw, out var name, "name") is { } bad) throw Invalid(bad);
        return name ?? throw new QuerithmException("invalidGroup", "Enter the group name.", Field("name"));
    }

    static QuerithmException Invalid(ProfileProblem p) => new("invalidGroup", p.Message, p.Field is { } f ? Field(f) : null);
    static QuerithmException Exists() => new("groupExists", "A group with this name already exists.", Field("name"));
    static QuerithmException NotFound() => new("notFound", "This group no longer exists (it may have been renamed or deleted).");
    static Dictionary<string, string> Field(string field) => new() { ["field"] = field };
}
