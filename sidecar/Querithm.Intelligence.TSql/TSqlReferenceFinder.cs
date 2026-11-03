using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.TSql;

/// <summary>
/// References to a schema object or column on the ScriptDom AST: table references, column references
/// with their aliases resolved per query scope, procedure and function calls, user types, sequences and module headers.
/// Strings and comments are never matched. An unqualified column is exact only when one table in scope has it.
/// </summary>
public sealed class TSqlReferenceFinder : IReferenceFinder
{
    static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    /// <summary>One parser per thread: building a parser costs more than parsing a small module.</summary>
    static readonly ThreadLocal<TSqlParser> Parsers = new(TSqlParsing.NewParser);

    static TSqlFragment? Parse(string text, out bool failed)
    {
        var fragment = Parsers.Value!.Parse(new StringReader(text), out var errors);
        failed = errors.Count > 0;
        return fragment;
    }

    public RefScan Find(string text, RefTarget target, RefContext ctx)
    {
        if (text.Length >= ChunkedDiagnostics.MinLength && !(target.Column != null && DefinesTrigger(text)) && FindChunked(text, target, ctx) is { } chunked) return chunked;
        var fragment = Parse(text, out var failed);
        if (fragment == null) return new RefScan([], false, true);
        var v = new Finder(target, ctx);
        fragment.Accept(v);
        return new RefScan(v.Hits.Values.OrderBy(h => h.Offset).ToList(), v.Dynamic, failed);
    }

    /// <summary>
    /// CREATE / ALTER TRIGGER in the text, from its tokens. A column then cannot be found per chunk: the inserted /
    /// deleted pseudo-tables and UPDATE(col) refer to the trigger's table, which only the chunk holding the header names.
    /// </summary>
    static bool DefinesTrigger(string text)
    {
        if (!RefText.ContainsWord(text, "trigger", 0, text.Length)) return false;
        var tokens = Parsers.Value!.GetTokenStream(new StringReader(text), out _);
        TSqlTokenType? last = null;
        foreach (var t in tokens)
        {
            if (t.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment) continue;
            if (t.TokenType == TSqlTokenType.Trigger && last is TSqlTokenType.Create or TSqlTokenType.Alter) return true;
            last = t.TokenType;
        }
        return false;
    }

    /// <summary>
    /// A large text (a 12k-line procedure costs 200–400 ms to parse whole) is cut at statement boundaries like the
    /// diagnostics do; only the chunks that mention the name — or might build dynamic SQL — are parsed, in parallel.
    /// </summary>
    static RefScan? FindChunked(string text, RefTarget target, RefContext ctx)
    {
        var chunks = ChunkedDiagnostics.Split(text);
        if (chunks.Count < 2) return null;
        var needle = target.Column ?? target.Name;
        var hits = new System.Collections.Concurrent.ConcurrentBag<RefHit>();
        var dynamic = 0;
        var failed = 0;
        Parallel.ForEach(chunks, c =>
        {
            var length = c.End - c.Start;
            // Chunks end at statement boundaries, so a column of the target table is only in a chunk that names the table.
            var named = RefText.ContainsWord(text, needle, c.Start, length) && (target.Column == null || RefText.ContainsWord(text, target.Name, c.Start, length));
            if (!named && !RefText.ContainsWord(text, "exec", c.Start, length) && !RefText.ContainsWord(text, "execute", c.Start, length)) return;
            var wrapped = ChunkedDiagnostics.Wrap(text, c);
            var prefix = 6 * c.StartDepth;
            var fragment = Parse(wrapped, out var f);
            if (f) Interlocked.Exchange(ref failed, 1);
            if (fragment == null) return;
            var v = new Finder(target, ctx);
            fragment.Accept(v);
            if (v.Dynamic) Interlocked.Exchange(ref dynamic, 1);
            foreach (var h in v.Hits.Values)
                if (h.Offset >= prefix && h.Offset - prefix + h.Length <= length) hits.Add(h with { Offset = c.Start + h.Offset - prefix });
        });
        return new RefScan(hits.OrderBy(h => h.Offset).ToList(), dynamic == 1, failed == 1);
    }

    public RefTarget? TargetAt(string text, int offset, RefContext ctx)
    {
        var fragment = Parse(text, out _);
        if (fragment == null) return null;
        var v = new Locator(offset, ctx);
        fragment.Accept(v);
        return v.Found;
    }

    public IReadOnlyList<(int Offset, int Length)>? LocalOccurrences(string text, int offset)
    {
        var fragment = Parse(text, out _);
        if (fragment == null) return null;
        var v = new Locals(offset);
        fragment.Accept(v);
        return v.Result();
    }

    static bool Regular(string s) => s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    /// <summary>
    /// A regular name that ScriptDom's lexer reads as one plain identifier. A reserved keyword (USER, ORDER, SELECT …)
    /// comes back as its own token type and needs brackets (L9); an unreserved one (NAME, PASSWORD) stays an identifier.
    /// </summary>
    static bool Bare(string s)
    {
        if (!Regular(s)) return false;
        var tokens = Parsers.Value!.GetTokenStream(new StringReader(s), out var errors);
        return errors.Count == 0 && tokens.Count(t => t.TokenType != TSqlTokenType.EndOfFile) == 1 && tokens[0].TokenType == TSqlTokenType.Identifier;
    }

    public string Rename(string original, string newName)
    {
        if (original.StartsWith('[')) return "[" + newName.Replace("]", "]]") + "]";
        if (original.StartsWith('"')) return "\"" + newName.Replace("\"", "\"\"") + "\"";
        return Bare(newName) ? newName : "[" + newName.Replace("]", "]]") + "]";
    }

    public bool ServerTracked(string definition, ObjectKind kind) => false;

    static bool Covers(TSqlFragment f, int offset) => f.StartOffset <= offset && offset <= f.StartOffset + f.FragmentLength;

    // ---------------------------------------------------------------------------------------------- scopes

    /// <summary>A table in a query's FROM (or a DML target): the alias it goes by, and the real table when it is one.</summary>
    sealed record ScopeTable(string? Alias, string? Schema, string? Name, Identifier? NameId, bool Unknown);

    /// <summary>Shared walk: query scopes (FROM, DML targets) pushed before their children are visited; CTE names per statement.</summary>
    abstract class ScopedVisitor(RefContext ctx) : TSqlFragmentVisitor
    {
        protected readonly RefContext Ctx = ctx;
        protected readonly List<List<ScopeTable>> Scopes = [];
        protected readonly HashSet<string> Ctes = new(Names);
        /// <summary>Inside a DML trigger: its table, which the inserted / deleted pseudo-tables stand for.</summary>
        protected (string Schema, string Name)? TriggerTable;

        public override void Visit(TriggerStatementBody node)
        {
            if (node.TriggerObject is { TriggerScope: TriggerScope.Normal, Name.BaseIdentifier.Value: { } table } t)
                TriggerTable = (SchemaOf(t.Name).Schema, table);
            base.Visit(node);
        }

        protected (string Schema, bool Qualified) SchemaOf(SchemaObjectName n)
        {
            if (n.SchemaIdentifier is { } s) return (s.Value, true);
            var name = n.BaseIdentifier?.Value ?? "";
            var schemas = Ctx.SchemasOf(name);
            if (schemas.Contains(Ctx.DefaultSchema, Names)) return (Ctx.DefaultSchema, false);
            if (schemas.Contains("dbo", Names)) return ("dbo", false);
            return (schemas.Count == 1 ? schemas.First() : Ctx.DefaultSchema, false);
        }

        ScopeTable Table(NamedTableReference n)
        {
            var o = n.SchemaObject;
            var name = o.BaseIdentifier?.Value;
            if (TriggerTable is { } tt && name != null && o.SchemaIdentifier == null && (Names.Equals(name, "inserted") || Names.Equals(name, "deleted")))
                return new ScopeTable(n.Alias?.Value ?? name, tt.Schema, tt.Name, null, false);
            if (name == null || o.DatabaseIdentifier != null || name.StartsWith('#') || (o.SchemaIdentifier == null && Ctes.Contains(name)))
                return new ScopeTable(n.Alias?.Value ?? name, null, null, null, true);
            var (schema, _) = SchemaOf(o);
            return new ScopeTable(n.Alias?.Value, schema, name, o.BaseIdentifier, false);
        }

        protected IEnumerable<ScopeTable> Collect(TableReference? t) => t switch
        {
            null => [],
            NamedTableReference n => [Table(n)],
            JoinTableReference j => Collect(j.FirstTableReference).Concat(Collect(j.SecondTableReference)),
            JoinParenthesisTableReference p => Collect(p.Join),
            TableReferenceWithAlias a => [new ScopeTable(a.Alias?.Value, null, null, null, true)],
            _ => [],
        };

        protected void Push(IEnumerable<ScopeTable> tables) => Scopes.Add(tables.ToList());
        protected void Pop() => Scopes.RemoveAt(Scopes.Count - 1);

        /// <summary>CTE names hold for one statement; ScriptDom visits the query before its WITH clause, so they are taken here.</summary>
        public override void Visit(TSqlStatement node)
        {
            if (Scopes.Count == 0) Ctes.Clear();
            if (node is StatementWithCtesAndXmlNamespaces { WithCtesAndXmlNamespaces: { } with })
                foreach (var c in with.CommonTableExpressions) Ctes.Add(c.ExpressionName.Value);
            base.Visit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            Push(node.FromClause?.TableReferences.SelectMany(Collect) ?? []);
            base.ExplicitVisit(node);
            Pop();
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            Push(Collect(node.Target).Concat(node.FromClause?.TableReferences.SelectMany(Collect) ?? []));
            base.ExplicitVisit(node);
            Pop();
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            Push(Collect(node.Target).Concat(node.FromClause?.TableReferences.SelectMany(Collect) ?? []));
            base.ExplicitVisit(node);
            Pop();
        }

        public override void ExplicitVisit(MergeSpecification node)
        {
            // The MERGE target's alias hangs off the merge itself, not off the table reference.
            var target = Collect(node.Target).Select(t => node.TableAlias != null ? t with { Alias = node.TableAlias.Value } : t);
            Push(target.Concat(Collect(node.TableReference)));
            base.ExplicitVisit(node);
            Pop();
        }

        /// <summary>The insert's column list belongs to its target only; its SELECT has scopes of its own.</summary>
        public override void ExplicitVisit(InsertSpecification node)
        {
            node.Target?.Accept(this);
            Push(Collect(node.Target));
            foreach (var c in node.Columns) c.Accept(this);
            Pop();
            node.InsertSource?.Accept(this);
            node.OutputClause?.Accept(this);
            node.OutputIntoClause?.Accept(this);
        }

        /// <summary>The table an identifier qualifier names in scope (alias first, then an unaliased table's own name).</summary>
        protected ScopeTable? Qualifier(string q)
        {
            for (var i = Scopes.Count - 1; i >= 0; i--)
            {
                var byAlias = Scopes[i].FirstOrDefault(t => t.Alias != null && Names.Equals(t.Alias, q));
                if (byAlias != null) return byAlias;
                var byName = Scopes[i].FirstOrDefault(t => t.Alias == null && t.Name != null && Names.Equals(t.Name, q));
                if (byName != null) return byName;
            }
            return null;
        }

        /// <summary>
        /// The tables that may own an unqualified column: the innermost scope with a candidate decides. Unknown tables
        /// (derived, temp, CTE) may have any column, so they make the answer ambiguous.
        /// </summary>
        protected List<ScopeTable>? Owners(string column)
        {
            for (var i = Scopes.Count - 1; i >= 0; i--)
            {
                var owners = Scopes[i].Where(t => t.Unknown || (t.Schema != null && (Ctx.Columns(t.Schema, t.Name!)?.Contains(column, Names) ?? true))).ToList();
                if (owners.Count > 0) return owners;
            }
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------- find

    sealed class Finder(RefTarget target, RefContext ctx) : ScopedVisitor(ctx)
    {
        public readonly Dictionary<int, RefHit> Hits = [];
        public bool Dynamic;

        void Hit(Identifier? id, RefConfidence c, string? reason = null, bool definition = false)
        {
            if (id == null) return;
            Hits.TryAdd(id.StartOffset, new RefHit(id.StartOffset, id.FragmentLength, c, reason, definition));
        }

        bool IsObject => target.Kind is not RefKind.Column;

        /// <summary>A schema-object name that may be the target: exact when qualified (or resolving) to its schema.</summary>
        void Name(SchemaObjectName? n, bool definition = false)
        {
            if (n?.BaseIdentifier == null || !IsObject || n.DatabaseIdentifier != null && n.ServerIdentifier != null) return;
            if (!Names.Equals(n.BaseIdentifier.Value, target.Name)) return;
            var (schema, qualified) = SchemaOf(n);
            if (!Names.Equals(schema, target.Schema)) return;
            if (!qualified && Ctx.SchemasOf(target.Name).Count > 1)
                Hit(n.BaseIdentifier, RefConfidence.Possible, $"unqualified: resolves to {schema}.{target.Name} only for callers whose default schema has no {target.Name}", definition);
            else Hit(n.BaseIdentifier, RefConfidence.Exact, null, definition);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (target.Kind is RefKind.Table or RefKind.View && !(node.SchemaObject.SchemaIdentifier == null && Ctes.Contains(node.SchemaObject.BaseIdentifier?.Value ?? "")))
                Name(node.SchemaObject);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
        {
            if (target.Kind == RefKind.Function) Name(node.SchemaObject);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            var name = node.ProcedureReference?.ProcedureReference?.Name;
            if (target.Kind == RefKind.Procedure) Name(name);
            if (name?.BaseIdentifier?.Value is { } n && Names.Equals(n, "sp_executesql")) Dynamic = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableStringList node)
        {
            Dynamic = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (target.Kind == RefKind.Function && node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: > 0 } ids } &&
                Names.Equals(node.FunctionName.Value, target.Name) && Names.Equals(ids[^1].Value, target.Schema))
                Hit(node.FunctionName, RefConfidence.Exact);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UserDataTypeReference node)
        {
            if (target.Kind == RefKind.Type) Name(node.Name);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NextValueForExpression node)
        {
            if (target.Kind == RefKind.Sequence) Name(node.SequenceName);
            base.ExplicitVisit(node);
        }

        public override void Visit(ProcedureStatementBody node)
        {
            if (target.Kind == RefKind.Procedure) Name(node.ProcedureReference?.Name, definition: true);
            base.Visit(node);
        }

        public override void Visit(FunctionStatementBody node)
        {
            if (target.Kind == RefKind.Function) Name(node.Name, definition: true);
            base.Visit(node);
        }

        public override void Visit(ViewStatementBody node)
        {
            if (target.Kind is RefKind.View or RefKind.Table) Name(node.SchemaObjectName, definition: true);
            base.Visit(node);
        }

        /// <summary>CREATE TRIGGER … ON table: the table (or view) the trigger is on.</summary>
        public override void Visit(TriggerStatementBody node)
        {
            base.Visit(node);
            if (target.Kind is RefKind.View or RefKind.Table && node.TriggerObject?.TriggerScope == TriggerScope.Normal) Name(node.TriggerObject.Name);
        }

        /// <summary>IF UPDATE(col) inside a trigger names a column of the trigger's table.</summary>
        public override void ExplicitVisit(UpdateCall node)
        {
            if (target.Kind == RefKind.Column && TriggerTable is { } tt && Names.Equals(node.Identifier?.Value, target.Column) &&
                Names.Equals(tt.Schema, target.Schema) && Names.Equals(tt.Name, target.Name))
                Hit(node.Identifier, RefConfidence.Exact);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            var ids = node.MultiPartIdentifier?.Identifiers;
            if (ids is { Count: > 0 })
            {
                if (target.Kind == RefKind.Column) Column(ids);
                else if (target.Kind is RefKind.Table or RefKind.View && ids.Count >= 2) TableQualifier(ids);
            }
            base.ExplicitVisit(node);
        }

        bool IsTarget(ScopeTable t) => !t.Unknown && Names.Equals(t.Schema, target.Schema) && Names.Equals(t.Name, target.Name);

        void Column(IList<Identifier> ids)
        {
            var col = ids[^1];
            if (!Names.Equals(col.Value, target.Column)) return;
            if (ids.Count == 1)
            {
                var owners = Owners(col.Value);
                if (owners == null || !owners.Any(IsTarget)) return;
                if (owners.Count == 1) Hit(col, RefConfidence.Exact);
                else Hit(col, RefConfidence.Possible, $"{owners.Count} tables in scope may have a column {col.Value}");
                return;
            }
            if (ids.Count == 2)
            {
                if (Qualifier(ids[0].Value) is { } t && IsTarget(t)) Hit(col, RefConfidence.Exact);
                return;
            }
            // schema.table.column (or db.schema.table.column in the current database)
            if (Names.Equals(ids[^2].Value, target.Name) && Names.Equals(ids[^3].Value, target.Schema)) Hit(col, RefConfidence.Exact);
        }

        void TableQualifier(IList<Identifier> ids)
        {
            var table = ids[^2];
            if (!Names.Equals(table.Value, target.Name)) return;
            if (ids.Count >= 3)
            {
                if (Names.Equals(ids[^3].Value, target.Schema)) Hit(table, RefConfidence.Exact);
                return;
            }
            if (Qualifier(table.Value) is { Alias: null } t && IsTarget(t)) Hit(table, RefConfidence.Exact);
        }
    }

    // ---------------------------------------------------------------------------------------------- target at cursor

    sealed class Locator(int offset, RefContext ctx) : ScopedVisitor(ctx)
    {
        public RefTarget? Found;

        RefTarget? Object(SchemaObjectName n, RefKind kind)
        {
            var (schema, _) = SchemaOf(n);
            return new RefTarget(kind, schema, n.BaseIdentifier.Value);
        }

        void Take(RefTarget? t) => Found ??= t;

        public override void ExplicitVisit(NamedTableReference node)
        {
            var o = node.SchemaObject;
            if (o.BaseIdentifier != null && Covers(o.BaseIdentifier, offset) && o.DatabaseIdentifier == null &&
                !(o.SchemaIdentifier == null && Ctes.Contains(o.BaseIdentifier.Value)) && !o.BaseIdentifier.Value.StartsWith('#'))
                Take(Object(o, RefKind.Table));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference?.ProcedureReference?.Name is { BaseIdentifier: { } b } n && Covers(b, offset)) Take(Object(n, RefKind.Procedure));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: > 0 } ids } && Covers(node.FunctionName, offset))
                Take(new RefTarget(RefKind.Function, ids[^1].Value, node.FunctionName.Value));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
        {
            if (node.SchemaObject.BaseIdentifier is { } b && Covers(b, offset)) Take(Object(node.SchemaObject, RefKind.Function));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UserDataTypeReference node)
        {
            if (node.Name.BaseIdentifier is { } b && Covers(b, offset)) Take(Object(node.Name, RefKind.Type));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            var ids = node.MultiPartIdentifier?.Identifiers;
            if (ids is { Count: > 0 } && Found == null)
            {
                var col = ids[^1];
                if (Covers(col, offset))
                {
                    ScopeTable? owner = null;
                    if (ids.Count == 1) owner = Owners(col.Value) is [{ Unknown: false } only] ? only : null;
                    else if (ids.Count == 2) owner = Qualifier(ids[0].Value);
                    else owner = new ScopeTable(null, ids[^3].Value, ids[^2].Value, null, false);
                    if (owner is { Unknown: false, Schema: { } s, Name: { } t }) Take(new RefTarget(RefKind.Column, s, t, col.Value));
                }
                else if (ids.Count >= 2 && Covers(ids[^2], offset))
                {
                    var q = ids.Count >= 3 ? new ScopeTable(null, ids[^3].Value, ids[^2].Value, null, false) : Qualifier(ids[^2].Value);
                    if (q is { Unknown: false, Schema: { } s, Name: { } t } && (ids.Count >= 3 || q.Alias == null)) Take(new RefTarget(RefKind.Table, s, t));
                }
            }
            base.ExplicitVisit(node);
        }

        public override void Visit(ProcedureStatementBody node)
        {
            if (node.ProcedureReference?.Name is { BaseIdentifier: { } b } n && Covers(b, offset)) Take(Object(n, RefKind.Procedure));
            base.Visit(node);
        }

        public override void Visit(ViewStatementBody node)
        {
            if (node.SchemaObjectName?.BaseIdentifier is { } b && Covers(b, offset)) Take(Object(node.SchemaObjectName, RefKind.View));
            base.Visit(node);
        }

        public override void Visit(FunctionStatementBody node)
        {
            if (node.Name?.BaseIdentifier is { } b && Covers(b, offset)) Take(Object(node.Name, RefKind.Function));
            base.Visit(node);
        }
    }

    // ---------------------------------------------------------------------------------------------- local symbols

    /// <summary>Variables and parameters (per batch), CTE names (per statement) and table aliases (per query).</summary>
    sealed class Locals(int offset) : TSqlFragmentVisitor
    {
        readonly List<(string Name, int Offset, int Length, TSqlBatch? Batch)> _variables = [];
        readonly List<(string Name, Identifier Id, TSqlStatement Statement)> _cteNames = [];
        readonly List<(string Name, Identifier Id, TSqlStatement Statement)> _cteUses = [];
        readonly List<(string Name, Identifier Id, QuerySpecification Query)> _aliases = [];
        readonly List<(string Name, Identifier Id, QuerySpecification? Query, TSqlStatement Statement)> _qualifiers = [];
        TSqlBatch? _batch;
        TSqlStatement? _statement;
        readonly Stack<QuerySpecification> _queries = new();

        public override void ExplicitVisit(TSqlBatch node) { _batch = node; base.ExplicitVisit(node); }

        public override void Visit(TSqlStatement node)
        {
            if (_queries.Count == 0) _statement = node;
            base.Visit(node);
        }

        public override void ExplicitVisit(VariableReference node) { _variables.Add((node.Name, node.StartOffset, node.FragmentLength, _batch)); base.ExplicitVisit(node); }
        public override void ExplicitVisit(DeclareVariableElement node) { _variables.Add((node.VariableName.Value, node.VariableName.StartOffset, node.VariableName.FragmentLength, _batch)); base.ExplicitVisit(node); }
        public override void ExplicitVisit(ProcedureParameter node) { _variables.Add((node.VariableName.Value, node.VariableName.StartOffset, node.VariableName.FragmentLength, _batch)); base.ExplicitVisit(node); }

        public override void ExplicitVisit(CommonTableExpression node)
        {
            if (_statement != null) _cteNames.Add((node.ExpressionName.Value, node.ExpressionName, _statement));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            var o = node.SchemaObject;
            if (o.SchemaIdentifier == null && o.BaseIdentifier != null && _statement != null) _cteUses.Add((o.BaseIdentifier.Value, o.BaseIdentifier, _statement));
            if (node.Alias != null && _queries.Count > 0) _aliases.Add((node.Alias.Value, node.Alias, _queries.Peek()));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QueryDerivedTable node)
        {
            if (node.Alias != null && _queries.Count > 0) _aliases.Add((node.Alias.Value, node.Alias, _queries.Peek()));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            _queries.Push(node);
            base.ExplicitVisit(node);
            _queries.Pop();
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier?.Identifiers is { Count: >= 2 } ids && _statement != null)
                _qualifiers.Add((ids[0].Value, ids[0], _queries.Count > 0 ? _queries.Peek() : null, _statement));
            base.ExplicitVisit(node);
        }

        public IReadOnlyList<(int Offset, int Length)>? Result()
        {
            var v = _variables.FirstOrDefault(x => x.Offset <= offset && offset <= x.Offset + x.Length);
            if (v.Name != null)
                return _variables.Where(x => x.Batch == v.Batch && Names.Equals(x.Name, v.Name)).Select(x => (x.Offset, x.Length)).Distinct().OrderBy(x => x.Offset).ToList();

            var cte = _cteNames.FirstOrDefault(x => Covers(x.Id, offset));
            if (cte.Name == null)
            {
                var use = _cteUses.FirstOrDefault(x => Covers(x.Id, offset));
                if (use.Name != null) cte = _cteNames.FirstOrDefault(x => x.Statement == use.Statement && Names.Equals(x.Name, use.Name));
            }
            if (cte.Name != null)
                return new[] { (cte.Id.StartOffset, cte.Id.FragmentLength) }
                    .Concat(_cteUses.Where(x => x.Statement == cte.Statement && Names.Equals(x.Name, cte.Name)).Select(x => (x.Id.StartOffset, x.Id.FragmentLength)))
                    .Concat(_qualifiers.Where(x => x.Statement == cte.Statement && Names.Equals(x.Name, cte.Name)).Select(x => (x.Id.StartOffset, x.Id.FragmentLength)))
                    .Distinct().OrderBy(x => x.Item1).ToList();

            var alias = _aliases.FirstOrDefault(x => Covers(x.Id, offset));
            if (alias.Name == null)
            {
                var q = _qualifiers.FirstOrDefault(x => Covers(x.Id, offset));
                if (q.Name != null) alias = _aliases.FirstOrDefault(x => Names.Equals(x.Name, q.Name) && q.Query != null && Contains(x.Query, q.Query));
            }
            if (alias.Name != null)
                return new[] { (alias.Id.StartOffset, alias.Id.FragmentLength) }
                    .Concat(_qualifiers.Where(x => Names.Equals(x.Name, alias.Name) && x.Query != null && Contains(alias.Query, x.Query)).Select(x => (x.Id.StartOffset, x.Id.FragmentLength)))
                    .Distinct().OrderBy(x => x.Item1).ToList();
            return null;
        }

        static bool Contains(TSqlFragment outer, TSqlFragment inner) =>
            outer.StartOffset <= inner.StartOffset && inner.StartOffset + inner.FragmentLength <= outer.StartOffset + outer.FragmentLength;
    }
}
