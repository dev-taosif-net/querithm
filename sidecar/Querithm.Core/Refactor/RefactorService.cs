using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Compare;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;

namespace Querithm.Core.Refactor;

/// <summary>
/// refactor.*: what is at the cursor, every reference to a schema object or column in the database's cached
/// definitions and the open tabs, and a previewed rename whose script (M5's guarded wrapper) rewrites what it touches.
/// The target is always resolved against the sidecar's own snapshot.
/// </summary>
public sealed class RefactorService(SessionManager sessions, MetadataService metadata, ObjectService objects, DefinitionFetcher definitions, ProviderRegistry providers)
{
    /// <summary>Name lookups over one snapshot, built once per snapshot instance.</summary>
    sealed class Index
    {
        public required Dictionary<string, List<SnapObject>> ByName { get; init; }
        public required Dictionary<(string, string), SnapObject> ByQualified { get; init; }
    }

    readonly ConditionalWeakTable<SchemaSnapshot, Index> _indexes = new();
    /// <summary>Scans of one definition for one target: a repeated search (Search again, the rename preview) parses nothing.</summary>
    readonly ConcurrentDictionary<string, RefScan> _scans = new(StringComparer.Ordinal);

    static StringComparer NamesFor(IDbProvider p) => p.Traits.NameMatch;

    /// <summary>
    /// How an engine's renames behave (Phase D0e: one exhaustive table instead of SQL Server / else-PostgreSQL branches,
    /// so an engine without its own rules is refused rather than treated like one of them).
    /// </summary>
    sealed record RenameRules(
        bool ModuleNamesResolveInOwnSchema, bool ServerRenamesOwnHeader, bool SchemaBinding, bool ColumnRenameNeedsModel, bool RoutineOverloads, bool RelationsShareNamespace,
        bool RecreateDropsFirst = false);

    static RenameRules RulesFor(Engine e) => e switch
    {
        // SQL Server: sp_rename leaves module text alone; schema-bound modules block; one name space per schema (types apart).
        Engine.Mssql => new(ModuleNamesResolveInOwnSchema: true, ServerRenamesOwnHeader: false, SchemaBinding: true, ColumnRenameNeedsModel: true,
            RoutineOverloads: false, RelationsShareNamespace: false),
        // PostgreSQL: ALTER … RENAME rewrites the header; routines overload by arguments; relations and types share pg_class / pg_type.
        Engine.Pg => new(ModuleNamesResolveInOwnSchema: false, ServerRenamesOwnHeader: true, SchemaBinding: false, ColumnRenameNeedsModel: false,
            RoutineOverloads: true, RelationsShareNamespace: true),
        // MySQL / MariaDB: a module's bare names resolve in its own database; RENAME TABLE renames tables and views (one name
        // space), leaving every definition that names them alone; routines cannot be renamed (the dialect refuses).
        Engine.MySql => new(ModuleNamesResolveInOwnSchema: true, ServerRenamesOwnHeader: true, SchemaBinding: false, ColumnRenameNeedsModel: false,
            RoutineOverloads: false, RelationsShareNamespace: true, RecreateDropsFirst: true), // no CREATE OR REPLACE for routines and triggers
        _ => throw new QuerithmException("refactorUnsupported", $"Rename is not available for {ProfileRules.EngineName(e)} yet."),
    };

    // Triggers are not referenced by name from SQL (and a PostgreSQL trigger may share its table's name), so name lookups skip them.
    Index IndexOf(SchemaSnapshot snap, StringComparer names) => _indexes.GetValue(snap, s => new Index
    {
        ByName = s.Objects.Where(o => o.Kind != ObjectKind.Trigger).GroupBy(o => o.Name, names).ToDictionary(g => g.Key, g => g.ToList(), names),
        ByQualified = s.Objects.Where(o => o.Kind != ObjectKind.Trigger).GroupBy(o => (o.Schema, o.Name), new NamePairComparer(names)).ToDictionary(g => g.Key, g => g.First(), new NamePairComparer(names)),
    });

    RefContext Context(SchemaSnapshot? snap, string defaultSchema, StringComparer names)
    {
        if (snap == null) return new RefContext { DefaultSchema = defaultSchema, Columns = (_, _) => null, SchemasOf = _ => [] };
        var idx = IndexOf(snap, names);
        return new RefContext
        {
            DefaultSchema = defaultSchema,
            Columns = (s, t) => idx.ByQualified.TryGetValue((s, t), out var o) && o.Columns is { } c ? c.Select(x => x.Name).ToArray() : null,
            SchemasOf = n => idx.ByName.TryGetValue(n, out var list) ? list.Select(o => o.Schema).Distinct(names).ToArray() : [],
        };
    }

    /// <summary>dbo / public; where a database is the schema (MySQL, SQLite's main), the tab's database.</summary>
    static string DefaultSchema(IDbProvider p, string? database) => p.Traits.DefaultSchema ?? database ?? "";

    static RefKind Map(RefactorKind k) => (RefKind)(int)k;
    static RefactorKind Map(RefKind k) => (RefactorKind)(int)k;

    static RefactorKind KindOf(ObjectKind k) => k switch
    {
        ObjectKind.View => RefactorKind.View, ObjectKind.Proc => RefactorKind.Procedure, ObjectKind.Func or ObjectKind.Tvf => RefactorKind.Function,
        ObjectKind.Type => RefactorKind.Type, ObjectKind.Sequence => RefactorKind.Sequence, _ => RefactorKind.Table,
    };

    static IReferenceFinder Finder(IDbProvider p)
    {
        FeatureGuard.Require(p, Feature.References); // the provider's own reason first
        return p.References ?? throw new QuerithmException("refactorUnsupported", $"Find usages and rename are not available for {p.Engine} yet.");
    }

    // ---------------------------------------------------------------------------------------------- symbol at cursor

    public SymbolAtResult SymbolAt(SymbolAtParams p)
    {
        Session? s = p.SessionId != null ? sessions.Get(p.SessionId) : null;
        var provider = s?.Provider ?? providers.ForDialect(p.Dialect) ?? throw QuerithmException.Invalid($"No provider for {p.Dialect}.");
        var f = Finder(provider);
        if (p.Offset < 0 || p.Offset > p.Text.Length) throw QuerithmException.Invalid("The offset is outside the text.");
        if (f.LocalOccurrences(p.Text, p.Offset) is { Count: > 0 } local)
            return new SymbolAtResult { Local = local.Select(x => new TextSpan { Offset = x.Offset, Length = x.Length }).ToArray() };
        if (s == null) return new SymbolAtResult();
        var db = p.Database ?? s.Database;
        var snap = db == null ? null : metadata.Cache.Get(s.Profile.Id, db, s.Id);
        if (snap == null) return new SymbolAtResult();
        var names = NamesFor(provider);
        var t = f.TargetAt(p.Text, p.Offset, Context(snap, DefaultSchema(provider, db), names));
        if (t == null) return new SymbolAtResult();
        var resolved = Resolve(snap, names, new RefactorTarget { Kind = Map(t.Kind), Schema = t.Schema, Name = t.Name, Column = t.Column }, out _);
        return new SymbolAtResult { Target = resolved };
    }

    /// <summary>The target as the snapshot knows it (its real kind and spelling), or null with the reason.</summary>
    RefactorTarget? Resolve(SchemaSnapshot snap, StringComparer names, RefactorTarget t, out string? why)
    {
        why = null;
        var idx = IndexOf(snap, names);
        if (!idx.ByQualified.TryGetValue((t.Schema, t.Name), out var o)) { why = $"{t.Schema}.{t.Name} is not in the cached metadata."; return null; }
        if (t.Kind == RefactorKind.Column)
        {
            var c = o.Columns?.FirstOrDefault(x => names.Equals(x.Name, t.Column));
            if (c == null || o.Kind is not (ObjectKind.Table or ObjectKind.View)) { why = $"{o.Schema}.{o.Name} has no column {t.Column}."; return null; }
            return new RefactorTarget { Kind = RefactorKind.Column, Schema = o.Schema, Name = o.Name, Column = c.Name };
        }
        return new RefactorTarget { Kind = KindOf(o.Kind), Schema = o.Schema, Name = o.Name };
    }

    // ---------------------------------------------------------------------------------------------- find usages

    sealed record Found(SnapObject? Object, string? TabId, string Text, RefHit Hit);

    sealed class ScanResult
    {
        public required Session Session { get; init; }
        public required string Database { get; init; }
        public required SchemaSnapshot Snapshot { get; init; }
        public required IReferenceFinder Finder { get; init; }
        public required RefactorTarget Target { get; init; }
        public required StringComparer Names { get; init; }
        public List<Found> Found { get; } = [];
        public ConcurrentBag<UncheckedObject> Unchecked { get; } = [];
        public ConcurrentBag<SnapObject> ServerUpdated { get; } = [];
        public Dictionary<long, string> Definitions { get; } = [];
        public int Scanned;
    }

    ObjectRef Ref(ScanResult r, SnapObject o) => new()
    {
        ProfileId = r.Session.Profile.Id, Database = r.Database, Schema = o.Schema, Name = o.Name, Kind = o.Kind, ObjectId = o.Id,
    };

    async Task<ScanResult> ScanAsync(string sessionId, string? database, RefactorTarget target, OpenTabText[]? tabs, CancellationToken ct)
    {
        var s = sessions.Get(sessionId);
        var f = Finder(s.Provider);
        var db = database ?? s.Database ?? throw QuerithmException.Invalid("Choose a database.");
        await definitions.EnsureAsync(s.Id, db, ct).ConfigureAwait(false);
        var snap = metadata.Cache.Get(s.Profile.Id, db, s.Id);
        if (snap == null || snap.Objects.Length == 0) throw new QuerithmException("refactorMetadata", $"The metadata of {db} is not loaded yet. Open the database in the explorer and try again.");
        var names = NamesFor(s.Provider);
        var resolved = Resolve(snap, names, target, out var why) ?? throw new QuerithmException("refactorTarget", why!);
        var r = new ScanResult { Session = s, Database = db, Snapshot = snap, Finder = f, Target = resolved, Names = names };
        var rt = new RefTarget(Map(resolved.Kind), resolved.Schema, resolved.Name, resolved.Column);
        var needle = resolved.Column ?? resolved.Name;
        var defs = metadata.Store.Definitions(s.Profile.Id, db);
        var found = new ConcurrentBag<Found>();
        var modules = snap.Objects.Where(o => o.Kind is ObjectKind.View or ObjectKind.Proc or ObjectKind.Func or ObjectKind.Tvf or ObjectKind.Trigger).ToList();
        Parallel.ForEach(modules, new ParallelOptions { CancellationToken = ct }, o =>
        {
            if (!defs.TryGetValue(o.Id, out var def)) return;
            if (def == null)
            {
                r.Unchecked.Add(new UncheckedObject { Object = Ref(r, o), Reason = "Encrypted or not visible to this login (VIEW DEFINITION): its definition is not available, so it cannot be checked." });
                return;
            }
            // Cheap pre-filter: a module that does not name it as a word cannot reference it — nor a column of a table
            // it never names (the finder resolves a column only through its table in the same statement).
            if (!RefText.ContainsWord(def, needle) || (resolved.Column != null && !RefText.ContainsWord(def, resolved.Name))) return;
            Interlocked.Increment(ref r.Scanned);
            // Unqualified names in a module resolve against the module's schema (SQL Server) or the default one.
            var ctx = Context(snap, RulesFor(s.Provider.Engine).ModuleNamesResolveInOwnSchema ? o.Schema : DefaultSchema(s.Provider, db), names);
            // The snapshot's modify marker changes with the object, so a cached scan never outlives its definition.
            var key = $"{s.Profile.Id}|{db}|{o.Id}|{o.ModifyDate}|{def.Length}|{rt}";
            var scan = _scans.GetOrAdd(key, _ => f.Find(def, rt, ctx));
            if (scan.DynamicSql)
                r.Unchecked.Add(new UncheckedObject { Object = Ref(r, o), Reason = "Builds SQL at run time (EXEC / sp_executesql / EXECUTE): it may reference this, but that cannot be checked." });
            if (scan.Hits.Count == 0) return;
            if (f.ServerTracked(def, o.Kind)) r.ServerUpdated.Add(o);
            foreach (var h in scan.Hits) found.Add(new Found(o, null, def, h));
        });
        foreach (var o in modules) if (defs.TryGetValue(o.Id, out var d) && d != null) r.Definitions[o.Id] = d;
        if (_scans.Count > 20_000) _scans.Clear();
        var mentioned = (tabs ?? []).Where(t => t.Text.Length <= 4_000_000 && RefText.ContainsWord(t.Text, needle) &&
                                                (resolved.Column == null || RefText.ContainsWord(t.Text, resolved.Name)));
        Parallel.ForEach(mentioned, new ParallelOptions { CancellationToken = ct }, tab =>
        {
            var scan = f.Find(tab.Text, rt, Context(snap, DefaultSchema(s.Provider, db), names));
            foreach (var h in scan.Hits) found.Add(new Found(null, tab.TabId, tab.Text, h));
        });
        r.Found.AddRange(found.OrderBy(x => x.TabId == null ? 1 : 0).ThenBy(x => x.TabId).ThenBy(x => x.Object?.Schema).ThenBy(x => x.Object?.Name).ThenBy(x => x.Hit.Offset));
        return r;
    }

    static string HitId(Found f) => f.TabId != null ? $"t:{f.TabId}:{f.Hit.Offset}" : $"o:{f.Object!.Id}:{f.Hit.Offset}";

    UsageHit Hit(ScanResult r, Found f)
    {
        var text = f.Text;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, f.Hit.Offset - 1)) + 1;
        if (f.Hit.Offset == 0) lineStart = 0;
        var lineEnd = text.IndexOf('\n', f.Hit.Offset);
        if (lineEnd < 0) lineEnd = text.Length;
        var line = 1;
        for (var i = 0; i < lineStart; i++) if (text[i] == '\n') line++;
        var snippet = text[lineStart..lineEnd].Trim();
        return new UsageHit
        {
            Id = HitId(f), TabId = f.TabId, Object = f.Object == null ? null : Ref(r, f.Object), Line = line, Column = f.Hit.Offset - lineStart + 1,
            Offset = f.Hit.Offset, Length = f.Hit.Length, Snippet = snippet.Length > 200 ? snippet[..200] + "…" : snippet,
            Confidence = f.Hit.Confidence == RefConfidence.Exact ? UsageConfidence.Exact : UsageConfidence.Possible, Reason = f.Hit.Reason, Definition = f.Hit.Definition,
        };
    }

    public async Task<FindUsagesResult> FindUsagesAsync(FindUsagesParams p, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var r = await ScanAsync(p.SessionId, p.Database, p.Target, p.Tabs, ct).ConfigureAwait(false);
        return new FindUsagesResult
        {
            Target = r.Target, Hits = r.Found.Select(f => Hit(r, f)).ToArray(),
            Unchecked = r.Unchecked.OrderBy(u => u.Object.Schema).ThenBy(u => u.Object.Name).ToArray(),
            ServerUpdated = r.ServerUpdated.Distinct().Select(o => Ref(r, o)).ToArray(), ObjectsScanned = r.Scanned, ElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    // ---------------------------------------------------------------------------------------------- rename

    static string CheckedName(string name)
    {
        var n = name.Trim();
        if (n.Length == 0) throw QuerithmException.Invalid("Enter the new name.");
        if (n.Length > 128) throw QuerithmException.Invalid("Names are at most 128 characters.");
        if (n.Any(char.IsControl)) throw QuerithmException.Invalid("A name cannot contain control characters.");
        return n;
    }

    /// <summary>The major version from "SQL Server 16.00.4135" / "PostgreSQL 16.4".</summary>
    public static int Major(string? version) =>
        int.TryParse(new string((version ?? "").SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray()), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;

    static ModuleKind ModuleKindOf(ObjectKind k) => k switch
    {
        ObjectKind.View => ModuleKind.View, ObjectKind.Proc => ModuleKind.Procedure, ObjectKind.Trigger => ModuleKind.Trigger, _ => ModuleKind.Function,
    };

    /// <summary>Why the new name is taken (a sibling column, or an object the engine keeps in the same name space), or null.</summary>
    static string? Conflict(ScanResult r, RefactorTarget t, string oldName, string newName, Engine engine)
    {
        var rules = RulesFor(engine);
        if (r.Names.Equals(oldName, newName)) return null; // a change of case only
        var objects = r.Snapshot.Objects;
        if (t.Kind == RefactorKind.Column)
        {
            var owner = objects.FirstOrDefault(o => o.Kind is ObjectKind.Table or ObjectKind.View && r.Names.Equals(o.Schema, t.Schema) && r.Names.Equals(o.Name, t.Name));
            return owner?.Columns?.Any(c => r.Names.Equals(c.Name, newName)) == true ? $"{t.Schema}.{t.Name} already has a column named {newName}: pick another name." : null;
        }
        var self = objects.FirstOrDefault(o => r.Names.Equals(o.Schema, t.Schema) && r.Names.Equals(o.Name, t.Name) && KindOf(o.Kind) == t.Kind);
        static string Args(SnapObject? o) => string.Join(",", (o?.Params ?? []).Where(x => !x.Output).OrderBy(x => x.Ordinal).Select(x => x.Type.ToLowerInvariant()));
        bool Clashes(SnapObject o)
        {
            if (rules.RelationsShareNamespace)
            {
                // Tables, views, sequences and types share pg_class / pg_type; routines clash only on the same arguments.
                if (t.Kind is RefactorKind.Function or RefactorKind.Procedure)
                    return o.Kind is ObjectKind.Func or ObjectKind.Tvf or ObjectKind.Proc && Args(o) == Args(self);
                return o.Kind is ObjectKind.Table or ObjectKind.View or ObjectKind.Sequence or ObjectKind.Type;
            }
            // SQL Server: one name space per schema for objects (sys.objects); types live in sys.types.
            return (o.Kind == ObjectKind.Type) == (t.Kind == RefactorKind.Type);
        }
        var taken = objects.FirstOrDefault(o => o != self && r.Names.Equals(o.Schema, t.Schema) && r.Names.Equals(o.Name, newName) && Clashes(o));
        return taken == null ? null : $"{t.Schema}.{taken.Name} already exists ({taken.Kind.ToString().ToLowerInvariant()}): pick another name.";
    }

    /// <summary>Rewritten modules in drop order (dependents first) and create order (dependencies first), from the catalog's references.</summary>
    static (List<(SnapObject Obj, string After, bool SchemaBound)> Drop, List<(SnapObject Obj, string After, bool SchemaBound)> Create) Ordered(
        ScanResult r, List<(SnapObject Obj, string After, bool SchemaBound)> rewritten, SchemaModel model, List<string> warnings)
    {
        var pairs = new NamePairComparer(r.Names);
        var refs = model.Modules.GroupBy(m => (m.Schema, m.Name), pairs).ToDictionary(g => g.Key, g => g.First().References, pairs);
        // Keys sort by name for ties; the graph uses the old names, as the catalog does.
        string Key((SnapObject Obj, string After, bool SchemaBound) x) => $"{x.Obj.Schema}.{x.Obj.Name}\u0001{x.Obj.Id}";
        var byKey = rewritten.ToDictionary(Key);
        var nodes = rewritten.ToDictionary(Key, x => new ModelModule
        {
            Schema = x.Obj.Schema, Name = x.Obj.Name, Kind = ModuleKindOf(x.Obj.Kind), References = refs.GetValueOrDefault((x.Obj.Schema, x.Obj.Name)) ?? [],
        });
        var warned = new HashSet<string>();
        void Warn(string w) { if (warned.Add(w)) warnings.Add(w); }
        return (ModuleOrder.Drop(nodes, r.Names, Warn).Select(k => byKey[k]).ToList(), ModuleOrder.Create(nodes, r.Names, Warn).Select(k => byKey[k]).ToList());
    }

    public async Task<RenamePreviewResult> PreviewRenameAsync(RenameParams p, CancellationToken ct)
    {
        var newName = CheckedName(p.NewName);
        var r = await ScanAsync(p.SessionId, p.Database, p.Target, p.Tabs, ct).ConfigureAwait(false);
        var t = r.Target;
        var oldName = t.Column ?? t.Name;
        if (r.Names.Equals(oldName, newName) && oldName == newName) throw QuerithmException.Invalid("The new name is the same as the old one.");
        var engine = r.Session.Provider.Engine;
        var rules = RulesFor(engine);
        if (Conflict(r, t, oldName, newName, engine) is { } clash) throw new QuerithmException("refactorConflict", clash);
        var d = r.Session.Provider.SchemaCompare ?? throw new QuerithmException("refactorUnsupported", $"Rename scripts are not available for {engine} yet.");
        var include = new HashSet<string>(p.Include ?? [], StringComparer.Ordinal);
        var exclude = new HashSet<string>(p.Exclude ?? [], StringComparer.Ordinal);
        var warnings = new List<string>();
        bool Accepted(Found f) => f.Hit.Confidence == RefConfidence.Exact ? !exclude.Contains(HitId(f)) : include.Contains(HitId(f));
        var accepted = r.Found.Where(Accepted).ToList();
        var possibleLeft = r.Found.Count(f => f.Hit.Confidence == RefConfidence.Possible && !include.Contains(HitId(f)));
        if (possibleLeft > 0) warnings.Add($"{possibleLeft} possible reference(s) are left as they are: check them and tick the ones to rename.");
        if (!r.Unchecked.IsEmpty) warnings.Add($"{r.Unchecked.Count} object(s) cannot be checked (dynamic SQL or encrypted): review them by hand.");

        string Apply(string text, IEnumerable<Found> hits)
        {
            var result = text;
            foreach (var h in hits.OrderByDescending(h => h.Hit.Offset))
                result = result[..h.Hit.Offset] + r.Finder.Rename(text.Substring(h.Hit.Offset, h.Hit.Length), newName) + result[(h.Hit.Offset + h.Hit.Length)..];
            return result;
        }

        var serverUpdated = r.ServerUpdated.Select(o => o.Id).ToHashSet();
        var isSelf = (SnapObject o) => t.Kind != RefactorKind.Column && r.Names.Equals(o.Schema, t.Schema) && r.Names.Equals(o.Name, t.Name);
        var previews = new List<RenameObjectPreview>();
        var rewritten = new List<(SnapObject Obj, string After, bool SchemaBound)>();
        foreach (var g in r.Found.Where(f => f.Object != null).GroupBy(f => f.Object!.Id))
        {
            var o = g.First().Object!;
            var mine = g.Where(Accepted).ToList();
            // PostgreSQL renames a routine or view by ALTER … RENAME; its own header needs no rewrite.
            if (rules.ServerRenamesOwnHeader && isSelf(o)) mine.RemoveAll(f => f.Hit.Definition);
            var before = r.Definitions[o.Id];
            var after = Apply(before, mine);
            var byServer = serverUpdated.Contains(o.Id);
            previews.Add(new RenameObjectPreview
            {
                Key = $"o:{o.Id}", Object = Ref(r, o), Before = before, After = after, Hits = g.Select(f => Hit(r, f)).ToArray(), ServerUpdated = byServer,
            });
            if (mine.Count > 0 && !byServer) rewritten.Add((o, after, rules.SchemaBinding && d.NormalizeBody(before).Split(' ').Contains("SCHEMABINDING")));
            if (rules.SchemaBinding && mine.Count < g.Count(f => f.Hit.Confidence == RefConfidence.Exact) && d.NormalizeBody(before).Split(' ').Contains("SCHEMABINDING"))
                warnings.Add($"{o.Schema}.{o.Name} is schema-bound and still references the old name: the rename will fail until it is changed too.");
        }

        // The script: blockers out, the rename, blockers back with the new name, then every rewritten module.
        var steps = new List<MigrationStep>();
        void Step(MigrationPhase phase, string? sql, string comment) { if (!string.IsNullOrWhiteSpace(sql)) steps.Add(new MigrationStep(phase, sql, comment)); }
        var major = Major(r.Snapshot.ServerVersion);
        var table = new ModelTable { Schema = t.Schema, Name = t.Name, Columns = [] };
        // SQL Server: the catalog model gives what blocks a column rename and the references between schema-bound
        // modules (dropped dependents first, recreated dependencies first). PostgreSQL follows renames itself.
        var ordered = rules.SchemaBinding && rewritten.Count > 1 && rewritten.Any(x => x.SchemaBound);
        var model = rules.ColumnRenameNeedsModel && (t.Kind == RefactorKind.Column || ordered)
            ? await objects.OnConnectionAsync(r.Session.Id, r.Database, c => d.ReadModelAsync(c, ct), ct).ConfigureAwait(false)
            : null;
        var (dropOrder, createOrder) = ordered ? Ordered(r, rewritten, model!, warnings) : (rewritten, rewritten);
        foreach (var (o, _, sb) in dropOrder.Where(x => x.SchemaBound))
            Step(MigrationPhase.DropModules, d.DropModule(new ModelModule { Schema = o.Schema, Name = o.Name, Kind = ModuleKindOf(o.Kind) }), $"drop schema-bound {o.Schema}.{o.Name} (recreated below)");
        if (t.Kind == RefactorKind.Column)
        {
            if (model != null)
            {
                // SQL Server refuses to rename a column that computed columns, checks or filtered indexes use.
                var mt = model.Tables.FirstOrDefault(x => r.Names.Equals(x.Schema, t.Schema) && r.Names.Equals(x.Name, t.Name));
                if (mt != null)
                {
                    table = mt;
                    string Ren(string e) => d.RenameIdentifier(e, t.Column!, newName);
                    bool Uses(string e) => Ren(e) != e;
                    var computed = mt.Columns.Where(c => c.Computed != null && Uses(c.Computed)).ToList();
                    var computedNames = computed.Select(c => c.Name).ToHashSet(r.Names);
                    var checks = mt.Checks.Where(c => Uses(c.Expression)).ToList();
                    var indexes = mt.Indexes.Where(i => (i.Filter != null && Uses(i.Filter)) || i.Columns.Concat(i.Include ?? []).Any(computedNames.Contains)).ToList();
                    foreach (var i in indexes) Step(MigrationPhase.Tables, d.DropIndex(mt, i), $"drop index {i.Name} (it uses {t.Column})");
                    foreach (var c in checks) Step(MigrationPhase.Tables, d.DropConstraint(mt, c.Name), $"drop check {c.Name} (it uses {t.Column})");
                    foreach (var c in computed) Step(MigrationPhase.Tables, d.DropColumn(mt, c), $"drop computed column {c.Name} (it uses {t.Column})");
                    Step(MigrationPhase.Tables, d.RenameColumn(mt, t.Column!, newName), $"rename column {t.Schema}.{t.Name}.{t.Column} to {newName}");
                    foreach (var c in computed) Step(MigrationPhase.Tables, d.AddColumn(mt, c with { Computed = Ren(c.Computed!) }), $"re-add computed column {c.Name}");
                    foreach (var c in checks) Step(MigrationPhase.Keys, d.AddCheck(mt, c with { Expression = Ren(c.Expression) }), $"re-add check {c.Name}");
                    foreach (var i in indexes)
                        Step(MigrationPhase.Keys, d.CreateIndex(mt, i with
                        {
                            Filter = i.Filter == null ? null : Ren(i.Filter),
                            Columns = i.Columns.Select(c => r.Names.Equals(c, t.Column) ? newName : c).ToArray(),
                            Include = i.Include?.Select(c => r.Names.Equals(c, t.Column) ? newName : c).ToArray(),
                        }), $"re-create index {i.Name}");
                    if (computed.Count > 0) warnings.Add($"Computed column(s) {string.Join(", ", computed.Select(c => c.Name))} are dropped and re-added at the end of the table.");
                }
                else Step(MigrationPhase.Tables, d.RenameColumn(table, t.Column!, newName), $"rename column {t.Schema}.{t.Name}.{t.Column} to {newName}");
            }
            else Step(MigrationPhase.Tables, d.RenameColumn(table, t.Column!, newName), $"rename column {t.Schema}.{t.Name}.{t.Column} to {newName}");
        }
        else
        {
            if (rules.RoutineOverloads && t.Kind is RefactorKind.Function or RefactorKind.Procedure &&
                r.Snapshot.Objects.Count(o => r.Names.Equals(o.Schema, t.Schema) && r.Names.Equals(o.Name, t.Name)) > 1)
                throw new QuerithmException("refactorOverloaded", $"{t.Schema}.{t.Name} is overloaded: rename the overload you mean with ALTER FUNCTION … RENAME by hand.");
            Step(MigrationPhase.Tables, d.RenameObject(Map(t.Kind), t.Schema, t.Name, newName), $"rename {t.Kind.ToString().ToLowerInvariant()} {t.Schema}.{t.Name} to {newName}");
        }
        foreach (var (o, after, _) in createOrder)
        {
            var name = isSelf(o) ? newName : o.Name;
            var wanted = new ModelModule { Schema = o.Schema, Name = name, Kind = ModuleKindOf(o.Kind), Definition = after };
            var existing = new ModelModule { Schema = o.Schema, Name = o.Name, Kind = ModuleKindOf(o.Kind) };
            if (rules.RecreateDropsFirst && d.NeedsDropBeforeCreate(existing, wanted, major))
                Step(MigrationPhase.DropModules, d.DropModule(existing), $"drop {o.Schema}.{o.Name} (recreated below)");
            Step(MigrationPhase.Modules, d.CreateModule(wanted, major),
                $"{(isSelf(o) ? "rewrite the definition of" : "update")} {o.Schema}.{name}");
        }
        var tracked = previews.Where(x => x.ServerUpdated).Select(x => $"{x.Object.Schema}.{x.Object.Name}").ToList();
        // A PostgreSQL view follows the column but keeps its own output column name, so its readers keep working.
        var views = previews.Where(x => x.ServerUpdated && x.Object.Kind == ObjectKind.View).Select(x => $"{x.Object.Schema}.{x.Object.Name}").ToList();
        if (t.Kind == RefactorKind.Column && views.Count > 0)
            warnings.Add($"{string.Join(", ", views)} follow(s) the rename but keep(s) the output column name {t.Column}; rename that by hand if you want it too.");
        var header = string.Join("\n",
            "-- Querithm rename",
            $"-- {CommentText.Safe($"{t.Schema}.{t.Name}{(t.Column != null ? "." + t.Column : "")} → {newName} in {r.Session.Profile.Name} · {r.Database}")}",
            $"-- {rewritten.Count} object(s) rewritten{(tracked.Count > 0 ? $"; updated by the server itself: {CommentText.Safe(string.Join(", ", tracked))}" : "")}.",
            d.TransactionalDdl ? "-- One transaction: if any statement fails, nothing is changed." : "-- The engine commits each DDL statement: see below.");
        if (warnings.Count > 0) header += "\n-- Review:\n" + string.Join("\n", warnings.Select(w => $"--   {CommentText.Safe(w)}"));

        var tabs = r.Found.Where(f => f.TabId != null && Accepted(f)).GroupBy(f => f.TabId!).Select(g => new TabEdits
        {
            TabId = g.Key,
            Edits = g.OrderBy(f => f.Hit.Offset).Select(f => new TextEdit { Offset = f.Hit.Offset, Length = f.Hit.Length, Text = r.Finder.Rename(f.Text.Substring(f.Hit.Offset, f.Hit.Length), newName) }).ToArray(),
        }).ToArray();
        return new RenamePreviewResult
        {
            Objects = previews.OrderBy(x => x.ServerUpdated).ThenBy(x => x.Object.Schema).ThenBy(x => x.Object.Name).ToArray(), Tabs = tabs,
            Sql = d.Wrap(steps, header), Statements = steps.Count, Warnings = warnings.ToArray(),
            Unchecked = r.Unchecked.OrderBy(u => u.Object.Schema).ThenBy(u => u.Object.Name).ToArray(), Transactional = d.TransactionalDdl,
        };
    }
}
