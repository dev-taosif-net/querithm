using System.Runtime.CompilerServices;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.TSql.Completion;

/// <summary>Case-insensitive lookups over one snapshot, built once per snapshot instance.</summary>
sealed class SchemaIndex
{
    static readonly ConditionalWeakTable<SchemaSnapshot, SchemaIndex> Cache = new();

    public required SchemaSnapshot Snapshot { get; init; }
    public required Dictionary<string, List<SnapObject>> ByName { get; init; }
    public required string[] Schemas { get; init; }

    public static SchemaIndex? For(SchemaSnapshot? snap)
    {
        if (snap == null) return null;
        return Cache.GetValue(snap, s =>
        {
            var byName = new Dictionary<string, List<SnapObject>>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in s.Objects)
            {
                if (o.Kind == ObjectKind.Trigger) continue; // never named in queries
                if (!byName.TryGetValue(o.Name, out var list)) byName[o.Name] = list = [];
                list.Add(o);
            }
            var schemas = s.Objects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            return new SchemaIndex { Snapshot = s, ByName = byName, Schemas = schemas };
        });
    }

    /// <summary>Unqualified names resolve to dbo first, then any schema.</summary>
    public SnapObject? Find(string? schema, string name, Func<SnapObject, bool>? filter = null)
    {
        if (!ByName.TryGetValue(name, out var list)) return null;
        IEnumerable<SnapObject> candidates = filter == null ? list : list.Where(filter);
        if (schema != null) return candidates.FirstOrDefault(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));
        SnapObject? any = null;
        foreach (var o in candidates)
        {
            if (string.Equals(o.Schema, "dbo", StringComparison.OrdinalIgnoreCase)) return o;
            any ??= o;
        }
        return any;
    }

    public bool HasSchema(string name) => Schemas.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsRowset(SnapObject o) => o.Kind is ObjectKind.Table or ObjectKind.View or ObjectKind.Tvf or ObjectKind.Synonym;
}

/// <summary>The snapshots reachable from one request: the current database plus other databases on demand.</summary>
sealed class SchemaScope(ISchemaSource source, string? profileId, string? database)
{
    readonly Dictionary<string, SchemaIndex?> others = new(StringComparer.OrdinalIgnoreCase);
    SchemaIndex? current;
    bool loaded;

    public SchemaIndex? Current
    {
        get
        {
            if (!loaded)
            {
                loaded = true;
                try { current = SchemaIndex.For(source.Get(profileId, database)); } catch { current = null; }
            }
            return current;
        }
    }

    public string? Database => database ?? Current?.Snapshot.Database;

    public SchemaIndex? ForDatabase(string? db)
    {
        if (db == null || (Database != null && string.Equals(db, Database, StringComparison.OrdinalIgnoreCase))) return Current;
        if (profileId == null) return null;
        if (others.TryGetValue(db, out var hit)) return hit;
        SchemaIndex? idx = null;
        try
        {
            if (Databases().Any(d => string.Equals(d, db, StringComparison.OrdinalIgnoreCase))) idx = SchemaIndex.For(source.Get(profileId, db));
        }
        catch { idx = null; }
        others[db] = idx;
        return idx;
    }

    IReadOnlyList<string>? databases;
    public IReadOnlyList<string> Databases()
    {
        if (databases != null) return databases;
        try { databases = source.Databases(profileId); } catch { databases = []; }
        return databases;
    }

    public bool IsDatabase(string name) => Databases().Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves [db.][schema.]name against the right snapshot.</summary>
    public SnapObject? Find(string? db, string? schema, string name, Func<SnapObject, bool>? filter = null) =>
        ForDatabase(db)?.Find(schema, name, filter);
}
