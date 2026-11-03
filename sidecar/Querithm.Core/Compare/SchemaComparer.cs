using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Compare;

/// <summary>What changed in a table that exists on both sides, target → source.</summary>
public sealed class TableDiff
{
    public List<ModelColumn> Added { get; } = [];
    public List<ModelColumn> Dropped { get; } = [];
    /// <summary>(target column, source column).</summary>
    public List<(ModelColumn From, ModelColumn To)> Altered { get; } = [];
    public (ModelKey? From, ModelKey? To)? PrimaryKey { get; set; }
    public Matched<ModelKey> Uniques { get; set; } = new();
    public Matched<ModelCheck> Checks { get; set; } = new();
    public Matched<ModelIndex> Indexes { get; set; } = new();
    public Matched<ModelFk> Fks { get; set; } = new();
}

/// <summary>Elements matched by name, then by shape: only in the target, only in the source, or changed.</summary>
public sealed class Matched<T>
{
    public List<T> Removed { get; } = [];
    public List<T> Added { get; } = [];
    public List<(T From, T To)> Changed { get; } = [];
    /// <summary>Unchanged elements present on both sides (target, source).</summary>
    public List<(T From, T To)> Same { get; } = [];
    public bool Any => Removed.Count + Added.Count + Changed.Count > 0;
}

/// <summary>The two sides of one compare item.</summary>
public sealed record ComparePair(CompareObjectKind Kind, object? Source, object? Target, TableDiff? Table);

/// <summary>A finished compare: the UI's items plus everything the script builder needs.</summary>
public sealed class Comparison
{
    public required SchemaModel Source { get; init; }
    public required SchemaModel Target { get; init; }
    public required ISchemaCompareDialect Dialect { get; init; }
    public required List<CompareItem> Items { get; init; }
    public required List<PossibleRename> Renames { get; init; }
    public required Dictionary<string, ComparePair> Pairs { get; init; }
    public required List<string> Notes { get; init; }
}

/// <summary>
/// Compares two schema models of one engine. The source is first renamed into the target's schema when
/// two different schemas are compared. Renames are never inferred: a dropped and an added column of the same type are
/// reported as a <see cref="PossibleRename"/>, and the script drops and adds unless the user confirms it.
/// </summary>
public static class SchemaComparer
{
    /// <summary>The model restricted to one schema (null: all of it).</summary>
    public static SchemaModel Filter(SchemaModel m, string? schema, StringComparer names)
    {
        if (schema == null) return m;
        bool In(string s) => names.Equals(s, schema);
        return m with
        {
            Schemas = m.Schemas.Where(In).ToArray(), Tables = m.Tables.Where(t => In(t.Schema)).ToArray(),
            Modules = m.Modules.Where(x => In(x.Schema)).ToArray(), Sequences = m.Sequences.Where(x => In(x.Schema)).ToArray(),
            Types = m.Types.Where(x => In(x.Schema)).ToArray(), Extensions = [],
        };
    }

    /// <summary>The source model as if it lived in schema <paramref name="to"/> (schema-to-schema compare).</summary>
    public static SchemaModel MapSchema(SchemaModel m, string from, string to, ISchemaCompareDialect d)
    {
        var names = d.Names;
        if (names.Equals(from, to)) return m;
        string S(string s) => names.Equals(s, from) ? to : s;
        string? R(string? sql) => sql == null ? null : d.RenameSchema(sql, from, to);
        return m with
        {
            Schemas = m.Schemas.Select(S).ToArray(),
            Tables = m.Tables.Select(t => t with
            {
                Schema = S(t.Schema),
                Columns = t.Columns.Select(c => c with { Default = R(c.Default), Computed = R(c.Computed) }).ToArray(),
                Checks = t.Checks.Select(c => c with { Expression = R(c.Expression)! }).ToArray(),
                Indexes = t.Indexes.Select(i => i with { Definition = R(i.Definition), Filter = R(i.Filter) }).ToArray(),
                Fks = t.Fks.Select(f => f with { RefSchema = S(f.RefSchema) }).ToArray(),
            }).ToArray(),
            Modules = m.Modules.Select(x => x with
            {
                Schema = S(x.Schema), Definition = R(x.Definition), References = x.References.Select(r => r with { Schema = S(r.Schema) }).ToArray(),
            }).ToArray(),
            Sequences = m.Sequences.Select(x => x with { Schema = S(x.Schema) }).ToArray(),
            Types = m.Types.Select(x => x with { Schema = S(x.Schema), Definition = R(x.Definition) }).ToArray(),
        };
    }

    public static string Key(CompareObjectKind kind, string schema, string name, string? detail = null) =>
        $"{kind}:{schema}.{name}{(detail != null ? $"|{detail}" : "")}";

    static CompareObjectKind KindOf(ModuleKind k) => k switch
    {
        ModuleKind.View or ModuleKind.MaterializedView => CompareObjectKind.View,
        ModuleKind.Procedure => CompareObjectKind.Procedure,
        ModuleKind.Trigger => CompareObjectKind.Trigger,
        _ => CompareObjectKind.Function,
    };

    /// <summary>Signature for routines, table for triggers: what tells two same-named modules apart.</summary>
    public static string? ModuleDetail(ModelModule m) => m.Kind == ModuleKind.Trigger ? m.Table : m.Signature;

    public static string ModuleKey(ModelModule m) => Key(KindOf(m.Kind), m.Schema, m.Name, ModuleDetail(m));
    public static string TableKey(ModelTable t) => Key(CompareObjectKind.Table, t.Schema, t.Name);

    public static Comparison Compare(SchemaModel source, SchemaModel target, ISchemaCompareDialect d)
    {
        var names = d.Names;
        var items = new List<CompareItem>();
        var pairs = new Dictionary<string, ComparePair>(StringComparer.Ordinal);
        var renames = new List<PossibleRename>();
        var keyCmp = names; // keys embed names, so they compare the way the engine compares names

        void Add(CompareObjectKind kind, string schema, string name, string? detail, object? src, object? tgt, CompareClass cls,
            DiffTag[] tags, string[] changes, bool dataLoss, string? note, TableDiff? table = null)
        {
            var key = Key(kind, schema, name, detail);
            pairs[key] = new ComparePair(kind, src, tgt, table);
            items.Add(new CompareItem
            {
                Key = key, Kind = kind, Schema = schema, Name = name, Detail = detail, Class = cls, Tags = tags, Changes = changes, DataLoss = dataLoss, Note = note,
            });
        }

        // Schemas and extensions: presence only.
        foreach (var (s, t) in Join(source.Schemas, target.Schemas, x => x, names))
            Add(CompareObjectKind.Schema, s ?? t!, s ?? t!, null, s, t, s == null ? CompareClass.TargetOnly : t == null ? CompareClass.SourceOnly : CompareClass.Identical, [], [], false, null);
        foreach (var (s, t) in Join(source.Extensions, target.Extensions, x => x, StringComparer.Ordinal))
            Add(CompareObjectKind.Extension, "", s ?? t!, null, s, t, s == null ? CompareClass.TargetOnly : t == null ? CompareClass.SourceOnly : CompareClass.Identical, [], [], false, null);

        foreach (var (s, t) in Join(source.Types, target.Types, x => $"{x.Schema}.{x.Name}", names))
        {
            var any = s ?? t!;
            if (s == null || t == null)
            {
                Add(CompareObjectKind.Type, any.Schema, any.Name, null, s, t, s == null ? CompareClass.TargetOnly : CompareClass.SourceOnly, [], [], false, null);
                continue;
            }
            var changes = new List<string>();
            string? note = null;
            if (s.Kind != t.Kind) { changes.Add($"kind: {t.Kind} → {s.Kind}"); note = "The type's kind changed; rebuild it by hand."; }
            else if (s.Kind == "enum")
            {
                var added = (s.Values ?? []).Except(t.Values ?? [], StringComparer.Ordinal).ToArray();
                if (added.Length > 0) changes.Add($"values: + {string.Join(", ", added)}");
                if (!EnumAppendable(t.Values ?? [], s.Values ?? []))
                {
                    changes.Add("values removed or reordered");
                    note = "Enum values can only be added; removing or reordering them needs a rebuild of the type by hand.";
                }
            }
            else if (d.NormalizeExpression(s.Definition ?? "") != d.NormalizeExpression(t.Definition ?? ""))
            {
                changes.Add($"definition: {t.Definition} → {s.Definition}");
                note = "The type is used by columns; change it by hand (drop and recreate after moving the columns off it).";
            }
            Add(CompareObjectKind.Type, s.Schema, s.Name, null, s, t, changes.Count == 0 ? CompareClass.Identical : CompareClass.Different,
                changes.Count == 0 ? [] : [DiffTag.Options], changes.ToArray(), false, note);
        }

        foreach (var (s, t) in Join(source.Sequences, target.Sequences, x => $"{x.Schema}.{x.Name}", names))
        {
            var any = s ?? t!;
            if (s == null || t == null)
            {
                Add(CompareObjectKind.Sequence, any.Schema, any.Name, null, s, t, s == null ? CompareClass.TargetOnly : CompareClass.SourceOnly, [], [], false, null);
                continue;
            }
            var changes = new List<string>();
            void Prop(string what, string? a, string? b) { if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) changes.Add($"{what}: {a ?? "none"} → {b ?? "none"}"); }
            Prop("type", t.Type, s.Type);
            Prop("increment", t.Increment, s.Increment);
            Prop("min", t.Min, s.Min);
            Prop("max", t.Max, s.Max);
            Prop("cycle", t.Cycle ? "yes" : "no", s.Cycle ? "yes" : "no");
            Add(CompareObjectKind.Sequence, s.Schema, s.Name, null, s, t, changes.Count == 0 ? CompareClass.Identical : CompareClass.Different,
                changes.Count == 0 ? [] : [DiffTag.Options], changes.ToArray(), false, null);
        }

        foreach (var (s, t) in Join(source.Tables, target.Tables, x => $"{x.Schema}.{x.Name}", names))
        {
            var any = s ?? t!;
            if (s == null || t == null)
            {
                Add(CompareObjectKind.Table, any.Schema, any.Name, null, s, t, s == null ? CompareClass.TargetOnly : CompareClass.SourceOnly, [],
                    s == null ? ["table only in the target: dropping it deletes its rows"] : [], s == null, null);
                continue;
            }
            var (diff, changes, tags) = CompareTable(s, t, d);
            var key = TableKey(t);
            foreach (var r in PossibleRenames(diff)) renames.Add(new PossibleRename { TableKey = key, From = r.From, To = r.To, Type = r.Type });
            var loss = diff.Dropped.Count > 0 || diff.Altered.Any(a => TypeChanged(a.From, a.To));
            Add(CompareObjectKind.Table, s.Schema, s.Name, null, s, t, changes.Count == 0 ? CompareClass.Identical : CompareClass.Different,
                tags.ToArray(), changes.ToArray(), loss, null, diff);
        }

        foreach (var (s, t) in Join(source.Modules, target.Modules, ModuleKey, keyCmp))
        {
            var any = s ?? t!;
            var kind = KindOf(any.Kind);
            var detail = ModuleDetail(any);
            if (s == null || t == null)
            {
                Add(kind, any.Schema, any.Name, detail, s, t, s == null ? CompareClass.TargetOnly : CompareClass.SourceOnly, [], [], false,
                    any.Definition == null ? "Encrypted: the definition is not available, so it cannot be scripted." : null);
                continue;
            }
            if (s.Definition == null || t.Definition == null)
            {
                var both = s.Definition == null && t.Definition == null;
                Add(kind, s.Schema, s.Name, detail, s, t, both ? CompareClass.Identical : CompareClass.Different, both ? [] : [DiffTag.Body], [], false,
                    both ? "Encrypted on both sides: not compared." : $"Encrypted on the {(s.Definition == null ? "source" : "target")}: the bodies cannot be compared.");
                continue;
            }
            if (Text(d.WithoutHeader(s.Definition)) == Text(d.WithoutHeader(t.Definition)) && s.Kind == t.Kind && s.Returns == t.Returns)
            {
                Add(kind, s.Schema, s.Name, detail, s, t, CompareClass.Identical, [], [], false, null);
                continue;
            }
            var cosmetic = s.Kind == t.Kind && d.NormalizeBody(s.Definition) == d.NormalizeBody(t.Definition);
            var changes = new List<string>();
            if (s.Kind != t.Kind) changes.Add($"kind: {t.Kind} → {s.Kind}");
            if (s.Returns != t.Returns) changes.Add($"returns: {t.Returns} → {s.Returns}");
            changes.Add(cosmetic ? "whitespace, comments or letter case only" : "definition differs");
            Add(kind, s.Schema, s.Name, detail, s, t, CompareClass.Different, cosmetic ? [DiffTag.WhitespaceOnly] : [DiffTag.Body], changes.ToArray(), false, null);
        }

        var notes = source.NotCompared.Select(n => $"Source: {n}").Concat(target.NotCompared.Select(n => $"Target: {n}")).ToList();
        items.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        return new Comparison { Source = source, Target = target, Dialect = d, Items = items, Renames = renames, Pairs = pairs, Notes = notes };
    }

    static string Text(string s) => string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();

    /// <summary>The target's values keep their order inside the source's: the new ones can be added with ADD VALUE.</summary>
    public static bool EnumAppendable(string[] target, string[] source)
    {
        var j = 0;
        foreach (var v in source)
            if (j < target.Length && v == target[j]) j++;
        return j == target.Length;
    }

    static IEnumerable<(T? Source, T? Target)> Join<T>(IEnumerable<T> source, IEnumerable<T> target, Func<T, string> key, StringComparer names) where T : class
    {
        var t = new Dictionary<string, T>(names);
        foreach (var x in target) t.TryAdd(key(x), x);
        var seen = new HashSet<string>(names);
        foreach (var s in source)
        {
            var k = key(s);
            if (!seen.Add(k)) continue;
            yield return (s, t.GetValueOrDefault(k));
        }
        foreach (var (k, x) in t)
            if (!seen.Contains(k)) yield return (null, x);
    }

    public static bool TypeChanged(ModelColumn from, ModelColumn to) =>
        from.Computed == null && to.Computed == null && !string.Equals(from.Type, to.Type, StringComparison.OrdinalIgnoreCase);

    static (TableDiff Diff, List<string> Changes, List<DiffTag> Tags) CompareTable(ModelTable s, ModelTable t, ISchemaCompareDialect d)
    {
        var names = d.Names;
        var diff = new TableDiff();
        var changes = new List<string>();
        var tags = new HashSet<DiffTag>();
        string? Norm(string? e) => e == null ? null : d.NormalizeExpression(e);

        foreach (var (sc, tc) in Join(s.Columns, t.Columns, c => c.Name, names))
        {
            if (tc == null) { diff.Added.Add(sc!); changes.Add($"+ column {sc!.Name} {Describe(sc)}"); continue; }
            if (sc == null) { diff.Dropped.Add(tc); changes.Add($"− column {tc.Name} (its data is lost)"); continue; }
            var what = new List<string>();
            if (Norm(sc.Computed) != Norm(tc.Computed) || sc.Persisted != tc.Persisted) what.Add($"computed: {tc.Computed ?? "no"} → {sc.Computed ?? "no"}");
            else if (sc.Computed == null && !string.Equals(sc.Type, tc.Type, StringComparison.OrdinalIgnoreCase)) what.Add($"{tc.Type} → {sc.Type}");
            if (sc.Nullable != tc.Nullable) what.Add(sc.Nullable ? "NOT NULL → NULL" : "NULL → NOT NULL");
            if (Norm(sc.Default) != Norm(tc.Default)) what.Add($"default: {tc.Default ?? "none"} → {sc.Default ?? "none"}");
            if (sc.Identity != tc.Identity) what.Add($"identity: {tc.Identity ?? "no"} → {sc.Identity ?? "no"}");
            if (!string.Equals(sc.Collation, tc.Collation, StringComparison.OrdinalIgnoreCase)) what.Add($"collation: {tc.Collation ?? "default"} → {sc.Collation ?? "default"}");
            if (what.Count == 0) continue;
            diff.Altered.Add((tc, sc));
            changes.Add($"column {sc.Name}: {string.Join("; ", what)}");
        }
        if (diff.Added.Count + diff.Dropped.Count + diff.Altered.Count > 0) tags.Add(DiffTag.Columns);

        string KeyShape(ModelKey k) => string.Join(",", k.Columns.Select((c, i) => $"{c.ToLowerInvariant()}{(k.Descending is { } dd && i < dd.Length && dd[i] ? " desc" : "")}")) + "|" + k.Kind;
        if (s.PrimaryKey != null || t.PrimaryKey != null)
        {
            var same = s.PrimaryKey != null && t.PrimaryKey != null && KeyShape(s.PrimaryKey) == KeyShape(t.PrimaryKey) &&
                       (s.PrimaryKey.SystemNamed || t.PrimaryKey.SystemNamed || names.Equals(s.PrimaryKey.Name, t.PrimaryKey.Name));
            if (!same)
            {
                diff.PrimaryKey = (t.PrimaryKey, s.PrimaryKey);
                changes.Add($"primary key: {(t.PrimaryKey == null ? "none" : $"({string.Join(", ", t.PrimaryKey.Columns)})")} → {(s.PrimaryKey == null ? "none" : $"({string.Join(", ", s.PrimaryKey.Columns)})")}");
                tags.Add(DiffTag.Constraints);
            }
        }

        diff.Uniques = Match(s.Uniques, t.Uniques, k => k.Name, k => k.SystemNamed, KeyShape, names);
        diff.Checks = Match(s.Checks, t.Checks, c => c.Name, c => c.SystemNamed, c => d.NormalizeExpression(c.Expression), names);
        diff.Fks = Match(s.Fks, t.Fks, f => f.Name, f => f.SystemNamed,
            f => $"{string.Join(",", f.Columns).ToLowerInvariant()}>{f.RefSchema}.{f.RefTable}({string.Join(",", f.RefColumns)})|{f.OnDelete}|{f.OnUpdate}|{f.Deferrable}".ToLowerInvariant(), names);
        diff.Indexes = Match(s.Indexes, t.Indexes, i => i.Name, _ => false, i => IndexShape(i, d), names);
        Describe(changes, "unique key", diff.Uniques, k => k.Name);
        Describe(changes, "check", diff.Checks, k => k.Name);
        Describe(changes, "foreign key", diff.Fks, k => k.Name);
        Describe(changes, "index", diff.Indexes, k => k.Name);
        if (diff.Uniques.Any || diff.Checks.Any || diff.Fks.Any) tags.Add(DiffTag.Constraints);
        if (diff.Indexes.Any) tags.Add(DiffTag.Indexes);
        return (diff, changes, tags.Order().ToList());
    }

    static string Describe(ModelColumn c) => c.Computed != null ? $"AS {c.Computed}" : $"{c.Type}{(c.Nullable ? "" : " NOT NULL")}";

    static void Describe<T>(List<string> changes, string what, Matched<T> m, Func<T, string> name)
    {
        changes.AddRange(m.Added.Select(x => $"+ {what} {name(x)}"));
        changes.AddRange(m.Removed.Select(x => $"− {what} {name(x)}"));
        changes.AddRange(m.Changed.Select(x => $"~ {what} {name(x.To)}"));
    }

    /// <summary>The index definition without its name: structured fields on SQL Server, pg_get_indexdef tokens on PostgreSQL.</summary>
    static string IndexShape(ModelIndex i, ISchemaCompareDialect d)
    {
        if (i.Definition != null)
        {
            var tokens = d.NormalizeExpression(i.Definition).Split(' ').ToList();
            var at = tokens.FindIndex(x => x is "index" or "INDEX");
            if (at >= 0 && at + 1 < tokens.Count) tokens.RemoveAt(at + 1);
            return string.Join(" ", tokens);
        }
        var cols = string.Join(",", i.Columns.Select((c, n) => $"{c.ToLowerInvariant()}{(i.Descending is { } dd && n < dd.Length && dd[n] ? " desc" : "")}"));
        var inc = string.Join(",", (i.Include ?? []).Select(x => x.ToLowerInvariant()).Order());
        return $"{i.Unique}|{i.Kind}|{cols}|{inc}|{(i.Filter == null ? "" : d.NormalizeExpression(i.Filter))}";
    }

    /// <summary>By name first; what is left is matched by shape (server-generated names differ between databases).</summary>
    static Matched<T> Match<T>(T[] source, T[] target, Func<T, string> name, Func<T, bool> systemNamed, Func<T, string> shape, StringComparer names) where T : class
    {
        var m = new Matched<T>();
        var leftTarget = target.ToList();
        var leftSource = new List<T>();
        foreach (var s in source)
        {
            var t = systemNamed(s) ? null : leftTarget.FirstOrDefault(x => !systemNamed(x) && names.Equals(name(x), name(s)));
            if (t == null) { leftSource.Add(s); continue; }
            leftTarget.Remove(t);
            if (shape(s) == shape(t)) m.Same.Add((t, s)); else m.Changed.Add((t, s));
        }
        foreach (var s in leftSource)
        {
            var t = leftTarget.FirstOrDefault(x => shape(x) == shape(s));
            if (t == null) { m.Added.Add(s); continue; }
            leftTarget.Remove(t);
            m.Same.Add((t, s));
        }
        m.Removed.AddRange(leftTarget);
        return m;
    }

    /// <summary>A dropped and an added column with the same type, when that pairing is the only one possible.</summary>
    static IEnumerable<(string From, string To, string Type)> PossibleRenames(TableDiff diff)
    {
        foreach (var dropped in diff.Dropped.Where(c => c.Computed == null))
        {
            var candidates = diff.Added.Where(a => a.Computed == null && string.Equals(a.Type, dropped.Type, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count != 1) continue;
            var others = diff.Dropped.Count(x => x.Computed == null && string.Equals(x.Type, dropped.Type, StringComparison.OrdinalIgnoreCase));
            if (others != 1) continue;
            yield return (dropped.Name, candidates[0].Name, dropped.Type);
        }
    }
}
