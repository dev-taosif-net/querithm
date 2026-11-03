using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Compare;

/// <summary>
/// The migration script for the checked items of a <see cref="Comparison"/>, in this order:
/// 1 FKs that are removed or touch altered columns · 2 removed modules and dependents of altered columns, dependents
/// first · 3 schemas, extensions, types, sequences · 4 tables (create, alter, drop) · 5 keys, checks, indexes · 6 FKs ·
/// 7 modules in dependency order, including the dependents dropped in 2. The dialect wraps it in one transaction.
/// </summary>
public sealed class MigrationScriptBuilder
{
    readonly Comparison _c;
    readonly ISchemaCompareDialect _d;
    readonly StringComparer _names;
    readonly NamePairComparer _pairs;
    /// <summary>Tables with a column rename that dependents block (SQL Server): their blocking modules go first, like an alter.</summary>
    readonly HashSet<string> _renameBlocked;
    readonly Dictionary<MigrationPhase, List<MigrationStep>> _steps = Enum.GetValues<MigrationPhase>().ToDictionary(p => p, _ => new List<MigrationStep>());
    readonly List<string> _warnings = [];
    readonly List<string> _skipped = [];

    public MigrationScriptBuilder(Comparison comparison)
    {
        _c = comparison;
        _d = comparison.Dialect;
        _names = _d.Names;
        _pairs = new NamePairComparer(_names);
        _renameBlocked = new HashSet<string>(_names);
    }

    int Major => _c.Target.MajorVersion;

    void Emit(MigrationPhase phase, string? sql, string comment)
    {
        if (!string.IsNullOrWhiteSpace(sql)) _steps[phase].Add(new MigrationStep(phase, sql, comment));
    }

    string Name(string schema, string name) => $"{schema}.{name}";
    string TableId(string schema, string name) => $"{schema}\u0001{name}";
    string TableId(ModelTable t) => TableId(t.Schema, t.Name);

    public SchemaMigrationResult Build(IReadOnlyCollection<string> keys, IReadOnlyList<PossibleRename> renames, bool includeDataLoss, string header)
    {
        var selected = new HashSet<string>(keys, StringComparer.Ordinal);
        var chosen = _c.Items.Where(i => selected.Contains(i.Key) && i.Class != CompareClass.Identical).ToList();
        ComparePair P(CompareItem i) => _c.Pairs[i.Key];

        // ------------------------------------------------------------------ 3. prerequisites
        var drops7 = new List<(string Sql, string Comment)>();
        foreach (var i in chosen.Where(i => i.Kind == CompareObjectKind.Schema))
        {
            if (i.Class == CompareClass.SourceOnly) Emit(MigrationPhase.Prerequisites, _d.CreateSchema(i.Name), $"create schema {i.Name}");
            else drops7.Add((_d.DropSchema(i.Name), $"drop schema {i.Name} (fails unless it is empty)"));
        }
        foreach (var i in chosen.Where(i => i.Kind == CompareObjectKind.Extension))
        {
            if (i.Class == CompareClass.SourceOnly) Emit(MigrationPhase.Prerequisites, _d.CreateExtension(i.Name), $"create extension {i.Name}");
            else drops7.Add((_d.DropExtension(i.Name), $"drop extension {i.Name}"));
        }
        var drops4 = new List<(string Sql, string Comment)>();
        foreach (var i in chosen.Where(i => i.Kind == CompareObjectKind.Type))
        {
            var p = P(i);
            var name = Name(i.Schema, i.Name);
            if (i.Class == CompareClass.SourceOnly) Emit(MigrationPhase.Prerequisites, _d.CreateType((ModelType)p.Source!), $"create type {name}");
            else if (i.Class == CompareClass.TargetOnly) drops4.Add((_d.DropType((ModelType)p.Target!), $"drop type {name}"));
            else
            {
                var (s, t) = ((ModelType)p.Source!, (ModelType)p.Target!);
                if (s.Kind == "enum" && t.Kind == "enum" && SchemaComparer.EnumAppendable(t.Values ?? [], s.Values ?? []))
                {
                    var values = s.Values ?? [];
                    for (var k = 0; k < values.Length; k++)
                        if (!(t.Values ?? []).Contains(values[k], StringComparer.Ordinal))
                            Emit(MigrationPhase.BeforeTransaction, _d.AddEnumValue(s, values[k], k > 0 ? values[k - 1] : null), $"enum {name}: add value {values[k]}");
                }
                else _warnings.Add($"Type {name} changed in a way that cannot be scripted automatically: {i.Note}");
            }
        }
        foreach (var i in chosen.Where(i => i.Kind == CompareObjectKind.Sequence))
        {
            var p = P(i);
            var name = Name(i.Schema, i.Name);
            if (i.Class == CompareClass.SourceOnly) Emit(MigrationPhase.Prerequisites, _d.CreateSequence((ModelSequence)p.Source!), $"create sequence {name}");
            else if (i.Class == CompareClass.TargetOnly) drops4.Add((_d.DropSequence((ModelSequence)p.Target!), $"drop sequence {name}"));
            else Emit(MigrationPhase.Prerequisites, _d.AlterSequence((ModelSequence)p.Target!, (ModelSequence)p.Source!), $"alter sequence {name}");
        }

        // ------------------------------------------------------------------ tables: what changes
        var droppedTables = new HashSet<string>(_names);
        var touched = new Dictionary<string, HashSet<string>>(_names); // table id → target columns dropped / retyped
        var plans = new List<TablePlan>();
        foreach (var i in chosen.Where(i => i.Kind == CompareObjectKind.Table))
        {
            var p = P(i);
            if (i.Class == CompareClass.TargetOnly)
            {
                var t = (ModelTable)p.Target!;
                if (!includeDataLoss) { _skipped.Add($"drop table {Name(t.Schema, t.Name)}"); continue; }
                droppedTables.Add(TableId(t));
                drops4.Insert(0, (_d.DropTable(t), $"drop table {Name(t.Schema, t.Name)} (data loss)"));
                continue;
            }
            if (i.Class == CompareClass.SourceOnly) { plans.Add(new TablePlan((ModelTable)p.Source!, null, null)); continue; }
            plans.Add(Plan((ModelTable)p.Source!, (ModelTable)p.Target!, p.Table!, renames.Where(r => r.TableKey == i.Key).ToList(), includeDataLoss, touched));
        }
        var planByTable = plans.Where(x => x.Target != null).ToDictionary(x => TableId(x.Target!), _names);
        var created = plans.Where(x => x.Target == null).Select(x => TableId(x.Source)).ToHashSet(_names);

        // ------------------------------------------------------------------ 1. FKs to drop (and re-add in 6)
        var fkReadd = new List<(ModelTable Table, ModelFk Fk)>();
        foreach (var t in _c.Target.Tables)
        {
            var id = TableId(t);
            if (droppedTables.Contains(id))
            {
                // Dropped with its table, except a key to another dropped table: that one goes first, so the tables
                // can be dropped in any order.
                foreach (var fk in t.Fks)
                {
                    var refId = TableId(fk.RefSchema, fk.RefTable);
                    if (!_names.Equals(refId, id) && droppedTables.Contains(refId))
                        Emit(MigrationPhase.DropFks, _d.DropConstraint(t, fk.Name), $"drop foreign key {fk.Name} on {Name(t.Schema, t.Name)} (both tables are dropped)");
                }
                continue;
            }
            planByTable.TryGetValue(id, out var plan);
            foreach (var fk in t.Fks)
            {
                var refId = TableId(fk.RefSchema, fk.RefTable);
                var removed = plan != null && (plan.Diff!.Fks.Removed.Contains(fk) || plan.Diff.Fks.Changed.Any(c => c.From == fk));
                var touchesOwn = touched.TryGetValue(id, out var own) && fk.Columns.Any(own.Contains);
                var touchesRef = touched.TryGetValue(refId, out var other) && fk.RefColumns.Any(other.Contains);
                var refKeyChanges = planByTable.TryGetValue(refId, out var refPlan) && refPlan.KeysChange;
                var refDropped = droppedTables.Contains(refId);
                if (!removed && !touchesOwn && !touchesRef && !refKeyChanges && !refDropped) continue;
                Emit(MigrationPhase.DropFks, _d.DropConstraint(t, fk.Name), $"drop foreign key {fk.Name} on {Name(t.Schema, t.Name)}");
                if (refDropped)
                {
                    if (plan == null) _warnings.Add($"Foreign key {fk.Name} on {Name(t.Schema, t.Name)} references {Name(fk.RefSchema, fk.RefTable)}, which is dropped; the key is dropped too.");
                    continue;
                }
                if (removed) continue;
                // Still wanted: the source's version when the table is being migrated, else the target's own.
                var wanted = plan?.Diff!.Fks.Same.FirstOrDefault(x => x.From == fk).To ?? fk;
                fkReadd.Add((plan?.Source ?? t, wanted));
            }
        }

        // ------------------------------------------------------------------ 2 / 7. modules
        // Module keys embed names: they compare the way the engine compares names (as the comparison paired them).
        var targetModules = _c.Target.Modules.ToDictionary(SchemaComparer.ModuleKey, _names);
        var sourceModules = _c.Source.Modules.ToDictionary(SchemaComparer.ModuleKey, _names);
        var drop = new Dictionary<string, ModelModule>(_names);   // target modules dropped in 2
        var create = new Dictionary<string, ModelModule>(_names); // versions created in 7
        var removedForGood = new HashSet<string>(_names);
        foreach (var i in chosen.Where(i => i.Kind is CompareObjectKind.View or CompareObjectKind.Function or CompareObjectKind.Procedure or CompareObjectKind.Trigger))
        {
            var p = P(i);
            var s = p.Source as ModelModule;
            var t = p.Target as ModelModule;
            if (s == null) { drop[i.Key] = t!; removedForGood.Add(i.Key); continue; }
            if (s.Definition == null) { _warnings.Add($"{Name(s.Schema, s.Name)} is encrypted on the source: it cannot be scripted."); continue; }
            if (_d.NeedsDropBeforeCreate(t, s, Major)) drop[i.Key] = t!;
            create[i.Key] = s;
        }
        // Dependents: modules that would block (or break with) a dropped or altered table or a dropped module.
        bool RefTable(ModelRef r, Func<string, bool> pred) => pred(TableId(r.Schema, r.Name));
        bool Changing(string id) => droppedTables.Contains(id) || touched.ContainsKey(id) || _renameBlocked.Contains(id);
        for (var changed = true; changed;)
        {
            changed = false;
            var dropped = drop.Values.Select(m => (m.Schema, m.Name)).ToHashSet(_pairs);
            foreach (var (key, m) in targetModules)
            {
                if (drop.ContainsKey(key) || !_d.DependencyBlocks(m)) continue;
                var onTable = m.References.Any(r => RefTable(r, Changing));
                var onModule = m.References.Any(r => dropped.Contains((r.Schema, r.Name)) && !_pairs.Equals((r.Schema, r.Name), (m.Schema, m.Name)));
                if (!onTable && !onModule) continue;
                drop[key] = m;
                changed = true;
                if (removedForGood.Contains(key)) continue;
                if (create.ContainsKey(key)) continue;
                var again = sourceModules.GetValueOrDefault(key) ?? m;
                if (again.Definition == null) { _warnings.Add($"{Name(m.Schema, m.Name)} depends on a changed object but is encrypted: it is dropped and cannot be recreated."); continue; }
                create[key] = again;
                _warnings.Add($"{Name(m.Schema, m.Name)} depends on a changed object: it is dropped and recreated.");
            }
        }
        // Modules that do not block a dropped table (not schema-bound on SQL Server) still break with it.
        foreach (var (key, m) in targetModules)
            if (!drop.ContainsKey(key) && m.References.Any(r => droppedTables.Contains(TableId(r.Schema, r.Name))))
                _warnings.Add($"{Name(m.Schema, m.Name)} reads a table that is dropped; it will fail until it is changed or dropped too.");
        foreach (var key in DropOrder(drop))
            Emit(MigrationPhase.DropModules, _d.DropModule(drop[key]), $"drop {drop[key].Kind.ToString().ToLowerInvariant()} {Name(drop[key].Schema, drop[key].Name)}");

        // ------------------------------------------------------------------ 4 / 5 / 6. tables
        foreach (var plan in plans)
        {
            if (plan.Target == null)
            {
                var t = plan.Source;
                var name = Name(t.Schema, t.Name);
                Emit(MigrationPhase.Tables, _d.CreateTable(t), $"create table {name}");
                foreach (var ix in t.Indexes) Emit(MigrationPhase.Keys, _d.CreateIndex(t, ix), $"create index {ix.Name} on {name}");
                foreach (var fk in t.Fks)
                {
                    var refId = TableId(fk.RefSchema, fk.RefTable);
                    if (!created.Contains(refId) && !_c.Target.Tables.Any(x => _names.Equals(TableId(x), refId)))
                        _warnings.Add($"Foreign key {fk.Name} on {name} references {Name(fk.RefSchema, fk.RefTable)}, which is not in the target and not selected.");
                    Emit(MigrationPhase.Fks, _d.AddFk(t, fk), $"add foreign key {fk.Name} on {name}");
                }
                continue;
            }
            foreach (var (phase, sql, comment) in plan.Steps) Emit(phase, sql, comment);
        }
        foreach (var (t, fk) in fkReadd) Emit(MigrationPhase.Fks, _d.AddFk(t, fk), $"re-add foreign key {fk.Name} on {Name(t.Schema, t.Name)}");
        foreach (var (sql, comment) in drops4) Emit(MigrationPhase.Tables, sql, comment);

        // ------------------------------------------------------------------ 7. modules, dependencies first
        foreach (var key in CreateOrder(create))
        {
            var m = create[key];
            Emit(MigrationPhase.Modules, _d.CreateModule(m, Major), $"{(targetModules.ContainsKey(key) && !drop.ContainsKey(key) ? "alter" : "create")} {m.Kind.ToString().ToLowerInvariant()} {Name(m.Schema, m.Name)}");
        }
        foreach (var (key, m) in targetModules)
        {
            if (drop.ContainsKey(key) || create.ContainsKey(key) || !m.References.Any(r => RefTable(r, id => touched.ContainsKey(id)))) continue;
            Emit(MigrationPhase.Modules, _d.RefreshModule(m), $"refresh {Name(m.Schema, m.Name)} (its tables changed)");
        }
        foreach (var (sql, comment) in drops7) Emit(MigrationPhase.Modules, sql, comment);

        var steps = _steps.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value).ToList();
        var fullHeader = header;
        if (_skipped.Count > 0) fullHeader += "\n-- Not included (data loss; tick \"Include data-loss changes\" to add them):\n" + string.Join("\n", _skipped.Select(s => $"--   {CommentText.Safe(s)}"));
        if (_warnings.Count > 0) fullHeader += "\n-- Review:\n" + string.Join("\n", _warnings.Select(s => $"--   {CommentText.Safe(s)}"));
        return new SchemaMigrationResult
        {
            Sql = steps.Count == 0 ? fullHeader + "\n\n-- Nothing to change.\n" : _d.Wrap(steps, fullHeader),
            Statements = steps.Count, Warnings = _warnings.ToArray(), Skipped = _skipped.ToArray(), Transactional = _d.TransactionalDdl,
        };
    }

    // ---------------------------------------------------------------------------------------------- one table

    sealed record TablePlan(ModelTable Source, ModelTable? Target, TableDiff? Diff)
    {
        public List<(MigrationPhase Phase, string? Sql, string Comment)> Steps { get; } = [];
        /// <summary>The primary key or a unique key changes: FKs that reference this table are dropped and re-added around it.</summary>
        public bool KeysChange { get; set; }
    }

    TablePlan Plan(ModelTable s, ModelTable t, TableDiff original, List<PossibleRename> renames, bool includeDataLoss, Dictionary<string, HashSet<string>> touchedByTable)
    {
        var plan = new TablePlan(s, t, original);
        var name = Name(t.Schema, t.Name);
        var d = original;
        void Step(MigrationPhase phase, string? sql, string comment) => plan.Steps.Add((phase, sql, comment));

        var dropped = d.Dropped.ToList();
        var added = d.Added.ToList();
        var altered = d.Altered.ToList();
        // Confirmed renames: the target column keeps its data under the new name. The renames run after what blocks
        // them is out of the way, and before anything that names the column by its new name.
        var renameSteps = new List<(string Sql, string Comment)>();
        var renamedFrom = new HashSet<string>(_names); // target names
        var renamedTo = new HashSet<string>(_names);
        foreach (var r in renames)
        {
            var oldCol = dropped.FirstOrDefault(c => _names.Equals(c.Name, r.From));
            var newCol = added.FirstOrDefault(c => _names.Equals(c.Name, r.To));
            if (oldCol == null || newCol == null) continue;
            dropped.Remove(oldCol);
            added.Remove(newCol);
            renameSteps.Add((_d.RenameColumn(t, oldCol.Name, newCol.Name), $"rename column {name}.{oldCol.Name} to {newCol.Name}"));
            renamedFrom.Add(oldCol.Name);
            renamedTo.Add(newCol.Name);
            var after = oldCol with { Name = newCol.Name };
            if (after with { DefaultName = newCol.DefaultName, DefaultSystemNamed = newCol.DefaultSystemNamed } != newCol) altered.Add((after, newCol));
        }

        if (!includeDataLoss)
        {
            foreach (var c in dropped) _skipped.Add($"drop column {name}.{c.Name}");
            dropped.Clear();
        }
        // Columns whose change needs the column dropped and added (computed ones), refused, or held back (data loss).
        var replace = new List<(ModelColumn From, ModelColumn To)>();
        var alter = new List<(ModelColumn From, ModelColumn To)>();
        foreach (var (was, now) in altered)
        {
            if (was.Computed != null || now.Computed != null)
            {
                if (was.Computed == null && !includeDataLoss) { _skipped.Add($"make {name}.{now.Name} computed (its stored values are lost)"); continue; }
                replace.Add((was, now));
                continue;
            }
            if (_d.AlterColumnRefusal(was, now) is { } why) { _warnings.Add($"{name}: {why}."); continue; }
            if (SchemaComparer.TypeChanged(was, now) && !includeDataLoss) { _skipped.Add($"change {name}.{now.Name} from {was.Type} to {now.Type}"); continue; }
            if (was.Nullable && !now.Nullable) _warnings.Add($"{name}.{now.Name} becomes NOT NULL: the script fails if it holds NULLs.");
            alter.Add((was, now));
        }

        // Target columns whose dependents must go first: dropped, replaced, and (where the engine needs it) altered.
        var touched = new HashSet<string>(_names);
        foreach (var c in dropped) touched.Add(c.Name);
        foreach (var (f, _) in replace) touched.Add(f.Name);
        foreach (var (f, to) in alter)
            if (SchemaComparer.TypeChanged(f, to) || !string.Equals(f.Collation, to.Collation, StringComparison.OrdinalIgnoreCase) ||
                (_d.AlterTypeNeedsDependentsDropped && _d.AlterColumn(t, f, to).Length > 0))
                touched.Add(f.Name);
        // Renamed columns whose dependents block the rename (SQL Server): computed columns, checks and filtered indexes
        // that use them go and come back, and schema-bound modules on the table are handled like for an alter.
        var renameBlocks = _d.RenameColumnNeedsDependentsDropped ? renamedFrom : new HashSet<string>(_names);
        if (renameBlocks.Count > 0) _renameBlocked.Add(TableId(t));
        // Computed columns that read a touched column block its change on both engines: drop and re-add them too.
        for (var more = true; more;)
        {
            more = false;
            foreach (var c in t.Columns)
            {
                if (c.Computed == null || touched.Contains(c.Name) || !(c.Uses ?? []).Any(u => touched.Contains(u) || renameBlocks.Contains(u))) continue;
                var wanted = s.Columns.FirstOrDefault(x => _names.Equals(x.Name, c.Name));
                if (wanted == null) continue; // dropped anyway
                replace.RemoveAll(r => _names.Equals(r.From.Name, c.Name));
                replace.Add((c, wanted));
                touched.Add(c.Name);
                more = true;
            }
        }
        if (touched.Count > 0) touchedByTable[TableId(t)] = touched;
        // Dropping a column takes its indexes and checks with it on every engine; retyping one needs them out of the way on SQL Server.
        var rebuilt = new HashSet<string>(dropped.Select(c => c.Name).Concat(replace.Select(r => r.From.Name)), _names);
        bool Touches(IEnumerable<string>? cols) =>
            cols != null && cols.Any(c => rebuilt.Contains(c) || (_d.AlterTypeNeedsDependentsDropped && touched.Contains(c)));
        // Expressions (check bodies, index filters) are read on the dialect's tokens, never as text.
        bool Reads(string? expression, string column) => expression != null && _d.RenameIdentifier(expression, column, column + "_q") != expression;
        var blockers = rebuilt.Concat(_d.AlterTypeNeedsDependentsDropped ? touched : []).Concat(renameBlocks).ToHashSet(_names);
        bool CheckTouches(ModelCheck c) => Touches(c.Columns) || (c.Columns ?? []).Any(renameBlocks.Contains) || blockers.Any(col => Reads(c.Expression, col));

        // Keys, checks and indexes: the removed and changed ones, plus (SQL Server) those on touched columns.
        ModelKey? dropPk = null, addPk = null;
        if (d.PrimaryKey != null || (t.PrimaryKey != null && Touches(t.PrimaryKey.Columns)))
        {
            dropPk = t.PrimaryKey;
            addPk = s.PrimaryKey;
        }
        var uniquesOut = d.Uniques.Removed.Concat(d.Uniques.Changed.Select(x => x.From)).Concat(d.Uniques.Same.Where(x => Touches(x.From.Columns)).Select(x => x.From)).ToList();
        var uniquesIn = d.Uniques.Added.Concat(d.Uniques.Changed.Select(x => x.To)).Concat(d.Uniques.Same.Where(x => Touches(x.From.Columns)).Select(x => x.To)).ToList();
        var checksOut = d.Checks.Removed.Concat(d.Checks.Changed.Select(x => x.From)).Concat(d.Checks.Same.Where(x => CheckTouches(x.From)).Select(x => x.From)).ToList();
        var checksIn = d.Checks.Added.Concat(d.Checks.Changed.Select(x => x.To)).Concat(d.Checks.Same.Where(x => CheckTouches(x.From)).Select(x => x.To)).ToList();
        // A filtered index blocks changes to the columns its WHERE reads, not only to its key and INCLUDE columns.
        bool IndexTouches(ModelIndex i) => Touches(i.Columns.Concat(i.Include ?? [])) || blockers.Any(col => Reads(i.Filter, col));
        var indexesOut = d.Indexes.Removed.Concat(d.Indexes.Changed.Select(x => x.From)).Concat(d.Indexes.Same.Where(x => IndexTouches(x.From)).Select(x => x.From)).ToList();
        var indexesIn = d.Indexes.Added.Concat(d.Indexes.Changed.Select(x => x.To)).Concat(d.Indexes.Same.Where(x => IndexTouches(x.From)).Select(x => x.To)).ToList();
        // Columns dropped for good take their indexes and checks with them; nothing to recreate on them.
        var gone = new HashSet<string>(dropped.Select(c => c.Name), _names);
        indexesIn.RemoveAll(i => i.Columns.Concat(i.Include ?? []).Any(gone.Contains) || gone.Any(col => Reads(i.Filter, col)));
        checksIn.RemoveAll(c => (c.Columns ?? []).Any(gone.Contains) || gone.Any(col => Reads(c.Expression, col)));
        uniquesIn.RemoveAll(u => u.Columns.Any(gone.Contains));
        plan.KeysChange = dropPk != null || uniquesOut.Count > 0 || indexesOut.Any(i => i.Unique);

        foreach (var i in indexesOut) Step(MigrationPhase.Tables, _d.DropIndex(t, i), $"drop index {i.Name} on {name}");
        foreach (var c in checksOut) Step(MigrationPhase.Tables, _d.DropConstraint(t, c.Name), $"drop check {c.Name} on {name}");
        foreach (var u in uniquesOut) Step(MigrationPhase.Tables, _d.DropConstraint(t, u.Name), $"drop unique key {u.Name} on {name}");
        if (dropPk != null) Step(MigrationPhase.Tables, _d.DropConstraint(t, dropPk.Name), $"drop primary key {dropPk.Name} on {name}");

        // Defaults: dropped where they change, and (SQL Server) on every column that is altered, replaced or dropped.
        // A renamed column is named by its new name (it is "was" here), so its steps wait for the rename.
        var afterRename = new List<(string? Sql, string Comment)>();
        void StepFor(string column, string? sql, string comment)
        {
            if (renamedTo.Contains(column)) afterRename.Add((sql, comment));
            else Step(MigrationPhase.Tables, sql, comment);
        }
        var defaultsBack = new List<ModelColumn>();
        foreach (var (was, now) in alter.Concat(replace))
        {
            var changes = _d.NormalizeExpression(was.Default ?? "") != _d.NormalizeExpression(now.Default ?? "");
            if (changes || (_d.AlterTypeNeedsDependentsDropped && touched.Contains(was.Name)))
            {
                StepFor(was.Name, _d.DropDefault(t, was), $"drop default of {name}.{was.Name}");
                if (now.Default != null && now.Computed == null) defaultsBack.Add(now);
            }
        }
        if (_d.AlterTypeNeedsDependentsDropped)
            foreach (var c in dropped) Step(MigrationPhase.Tables, _d.DropDefault(t, c), $"drop default of {name}.{c.Name}");

        foreach (var c in dropped) Step(MigrationPhase.Tables, _d.DropColumn(t, c), $"drop column {name}.{c.Name} (data loss)");
        foreach (var (was, _) in replace) StepFor(was.Name, _d.DropColumn(t, was), $"drop column {name}.{was.Name} to recreate it");
        foreach (var (sql, comment) in renameSteps) Step(MigrationPhase.Tables, sql, comment);
        foreach (var (sql, comment) in afterRename) Step(MigrationPhase.Tables, sql, comment);
        foreach (var (was, now) in alter)
            foreach (var sql in _d.AlterColumn(t, was, now)) Step(MigrationPhase.Tables, sql, $"alter column {name}.{now.Name}");
        foreach (var (_, now) in replace) Step(MigrationPhase.Tables, _d.AddColumn(t, now with { Default = null }), $"add column {name}.{now.Name}");
        foreach (var c in added)
        {
            if (!c.Nullable && c.Default == null && c.Identity == null && c.Computed == null)
                _warnings.Add($"{name}.{c.Name} is added as NOT NULL without a default: the script fails if the table has rows.");
            Step(MigrationPhase.Tables, _d.AddColumn(t, c), $"add column {name}.{c.Name}");
        }
        foreach (var c in defaultsBack) Step(MigrationPhase.Tables, _d.AddDefault(t, c), $"set default of {name}.{c.Name}");

        if (addPk != null) Step(MigrationPhase.Keys, _d.AddKey(s, addPk, true), $"add primary key {addPk.Name} on {name}");
        foreach (var u in uniquesIn) Step(MigrationPhase.Keys, _d.AddKey(s, u, false), $"add unique key {u.Name} on {name}");
        foreach (var c in checksIn) Step(MigrationPhase.Keys, _d.AddCheck(s, c), $"add check {c.Name} on {name}");
        foreach (var i in indexesIn) Step(MigrationPhase.Keys, _d.CreateIndex(s, i), $"create index {i.Name} on {name}");
        foreach (var f in d.Fks.Added.Concat(d.Fks.Changed.Select(x => x.To)))
            Step(MigrationPhase.Fks, _d.AddFk(s, f), $"add foreign key {f.Name} on {name}");
        return plan;
    }

    // ---------------------------------------------------------------------------------------------- module order

    IEnumerable<string> CreateOrder(Dictionary<string, ModelModule> modules) => ModuleOrder.Create(modules, _names, _warnings.Add);
    IEnumerable<string> DropOrder(Dictionary<string, ModelModule> modules) => ModuleOrder.Drop(modules, _names, _warnings.Add);

    // ---------------------------------------------------------------------------------------------- diff view

    /// <summary>One side of an item scripted from its model, so both sides of the diff come from the same generator.</summary>
    public static string? Script(ISchemaCompareDialect d, CompareObjectKind kind, object? obj, int major)
    {
        switch (obj)
        {
            case null: return null;
            case ModelTable t:
                var parts = new List<string> { d.CreateTable(t) };
                parts.AddRange(t.Indexes.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(i => d.CreateIndex(t, i)));
                parts.AddRange(t.Fks.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => d.AddFk(t, f)));
                return string.Join("\n\n", parts) + "\n";
            case ModelModule m: return m.Definition == null ? "-- Encrypted: the definition is not available.\n" : m.Definition.Trim() + "\n";
            case ModelSequence s: return d.CreateSequence(s) + "\n";
            case ModelType ty: return d.CreateType(ty) + "\n";
            case string name: return (kind == CompareObjectKind.Schema ? d.CreateSchema(name) : d.CreateExtension(name)) + "\n";
            default: return null;
        }
    }
}
