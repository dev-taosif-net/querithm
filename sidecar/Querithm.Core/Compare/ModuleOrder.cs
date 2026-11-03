using Querithm.Contract.Providers;

namespace Querithm.Core.Compare;

/// <summary>
/// Dependency order of modules from their references (Kahn's sort; ties: functions, views, procedures, triggers, then
/// name). Names compare the way the engine compares them. Shared by schema compare and safe rename.
/// </summary>
public static class ModuleOrder
{
    static int KindRank(ModuleKind k) => k switch
    {
        ModuleKind.Function => 0, ModuleKind.View => 1, ModuleKind.MaterializedView => 2, ModuleKind.Procedure => 3, _ => 4,
    };

    /// <summary>A module comes after the modules it references.</summary>
    public static IEnumerable<string> Create(Dictionary<string, ModelModule> modules, StringComparer names, Action<string> warn)
    {
        var pairs = new NamePairComparer(names);
        var byName = modules.GroupBy(kv => (kv.Value.Schema, kv.Value.Name), pairs).ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList(), pairs);
        var needs = modules.ToDictionary(kv => kv.Key, kv => kv.Value.References
            .Where(r => !pairs.Equals((r.Schema, r.Name), (kv.Value.Schema, kv.Value.Name)))
            .SelectMany(r => byName.GetValueOrDefault((r.Schema, r.Name)) ?? []).Where(k => !names.Equals(k, kv.Key)).ToHashSet(names), names);
        return Kahn(modules, needs, names, warn);
    }

    /// <summary>Dependents first: a module comes before the modules it references.</summary>
    public static IEnumerable<string> Drop(Dictionary<string, ModelModule> modules, StringComparer names, Action<string> warn)
    {
        var pairs = new NamePairComparer(names);
        var byName = modules.GroupBy(kv => (kv.Value.Schema, kv.Value.Name), pairs).ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList(), pairs);
        var needs = modules.ToDictionary(kv => kv.Key, _ => new HashSet<string>(names), names);
        foreach (var (key, m) in modules)
            foreach (var r in m.References)
                foreach (var dep in byName.GetValueOrDefault((r.Schema, r.Name)) ?? [])
                    if (!names.Equals(dep, key)) needs[dep].Add(key); // dep is dropped after key
        return Kahn(modules, needs, names, warn);
    }

    static List<string> Kahn(Dictionary<string, ModelModule> modules, Dictionary<string, HashSet<string>> needs, StringComparer names, Action<string> warn)
    {
        var done = new HashSet<string>(names);
        var order = new List<string>();
        var pending = modules.Keys.OrderBy(k => KindRank(modules[k].Kind)).ThenBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(k => needs[k].All(done.Contains));
            if (next == null)
            {
                // A cycle (or references the catalog could not resolve): keep the remaining tie order and say so.
                warn($"Circular references between {string.Join(", ", pending.Select(k => $"{modules[k].Schema}.{modules[k].Name}"))}: check the order.");
                order.AddRange(pending);
                break;
            }
            pending.Remove(next);
            done.Add(next);
            order.Add(next);
        }
        return order;
    }
}
