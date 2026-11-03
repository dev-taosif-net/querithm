using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;

namespace Querithm.Intelligence.TSql.Completion;

sealed record Col(string Name, string? Type, string? Note = null);

enum SourceKind { Table, View, Tvf, Synonym, Cte, Derived, TempTable, TableVar, Pseudo, Values, Pivot, Function, Unknown }

/// <summary>A row source in scope (FROM item, DML target, pseudo-table) with lazily computed columns.</summary>
sealed class Source(string exposed, SourceKind kind, string display, Func<IReadOnlyList<Col>> load)
{
    IReadOnlyList<Col>? cols;
    public string Exposed { get; } = exposed;
    public SourceKind Kind { get; } = kind;
    /// <summary>e.g. "dbo.Orders", "CTE recent", "derived table".</summary>
    public string Display { get; } = display;
    public bool HasAlias { get; init; }
    public IReadOnlyList<Col> Columns
    {
        get
        {
            if (cols != null) return cols;
            try { cols = load(); } catch { cols = []; }
            return cols;
        }
    }

    public Col? Column(string name)
    {
        foreach (var c in Columns) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }
}

sealed record VarDecl(string Name, string? Type, bool IsParam, bool Output = false, bool HasDefault = false);

sealed class TableDecl(string name, bool isVariable, Func<IReadOnlyList<Col>> load)
{
    IReadOnlyList<Col>? cols;
    public string Name { get; } = name;
    public bool IsVariable { get; } = isVariable;
    public IReadOnlyList<Col> Columns
    {
        get
        {
            if (cols != null) return cols;
            cols = []; // re-entrancy guard (SELECT INTO #t FROM #t)
            try { cols = load(); } catch { cols = []; }
            return cols;
        }
    }
}

sealed record ModuleParam(string Name, string Type, bool Output, bool HasDefault, string? Default);

sealed class ModuleInfo
{
    public required string Kind { get; init; }
    public string? Schema { get; init; }
    public required string Name { get; init; }
    public required List<ModuleParam> Params { get; init; }
    public SchemaObjectName? TriggerTarget { get; init; }
}

/// <summary>Declarations visible at the cursor: variables, parameters, temp tables, table variables, cursors.</summary>
sealed class Env(SchemaScope schema)
{
    public SchemaScope Schema { get; } = schema;
    public Dictionary<string, VarDecl> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TableDecl> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Cursors { get; } = [];
    public ModuleInfo? Module { get; set; }
    /// <summary>Every module header in the document, by lower-case name (for EXEC parameter defaults).</summary>
    public List<ModuleInfo> DocModules { get; } = [];
}

/// <summary>CTEs visible at a point: the statement's WITH list up to <see cref="Limit"/> (exclusive).</summary>
sealed class CteScope(IList<CommonTableExpression> list, int limit)
{
    public static readonly CteScope Empty = new([], 0);
    public IList<CommonTableExpression> List { get; } = list;
    public int Limit { get; } = Math.Min(limit, list.Count);

    public (CommonTableExpression Cte, int Index)? Find(string name)
    {
        for (var i = 0; i < Limit; i++)
            if (string.Equals(List[i].ExpressionName.Value, name, StringComparison.OrdinalIgnoreCase)) return (List[i], i);
        return null;
    }

    public CteScope Upto(int index) => new(List, index);

    public static CteScope Of(TSqlStatement? stmt) =>
        stmt is StatementWithCtesAndXmlNamespaces s && s.WithCtesAndXmlNamespaces?.CommonTableExpressions is { Count: > 0 } l ? new(l, l.Count) : Empty;
}

/// <summary>Resolves table references to sources and infers projection column names and types.</summary>
sealed class Resolver(Env env)
{
    readonly Dictionary<CommonTableExpression, IReadOnlyList<Col>> cteCols = new(ReferenceEqualityComparer.Instance);
    int depth;

    public Env Env => env;

    public List<Source> Flatten(TableReference? tr, CteScope ctes)
    {
        var list = new List<Source>();
        Flatten(tr, ctes, list);
        return list;
    }

    public List<Source> Flatten(FromClause? from, CteScope ctes)
    {
        var list = new List<Source>();
        if (from?.TableReferences != null) foreach (var t in from.TableReferences) Flatten(t, ctes, list);
        return list;
    }

    void Flatten(TableReference? tr, CteScope ctes, List<Source> into)
    {
        switch (tr)
        {
            case null: return;
            case JoinTableReference j:
                Flatten(j.FirstTableReference, ctes, into);
                Flatten(j.SecondTableReference, ctes, into);
                return;
            case JoinParenthesisTableReference jp:
                Flatten(jp.Join, ctes, into);
                return;
            case TableReferenceWithAlias a:
                if (FromRef(a, ctes) is { } s) into.Add(s);
                return;
        }
    }

    public Source? FromRef(TableReferenceWithAlias tr, CteScope ctes)
    {
        var alias = tr.Alias?.Value;
        switch (tr)
        {
            case NamedTableReference n:
            {
                var so = n.SchemaObject;
                var name = so.BaseIdentifier?.Value ?? "";
                return Named(so.DatabaseIdentifier?.Value, so.SchemaIdentifier?.Value, name, alias, ctes);
            }
            case VariableTableReference v:
            {
                var name = v.Variable.Name;
                env.Tables.TryGetValue(name, out var decl);
                return new Source(alias ?? name, SourceKind.TableVar, "table variable " + name, () => decl?.Columns ?? []) { HasAlias = alias != null };
            }
            case QueryDerivedTable q:
                return new Source(alias ?? "(derived)", SourceKind.Derived, "derived table", () => Rename(Projection(q.QueryExpression, ctes), q.Columns)) { HasAlias = alias != null };
            case InlineDerivedTable iv:
                return new Source(alias ?? "(values)", SourceKind.Values, "VALUES", () =>
                {
                    var first = iv.RowValues.Count > 0 ? iv.RowValues[0].ColumnValues : null;
                    return iv.Columns.Select((c, i) => new Col(c.Value, first != null && i < first.Count ? TypeOf(first[i], [], ctes) : null)).ToList();
                }) { HasAlias = alias != null };
            case SchemaObjectFunctionTableReference f:
            {
                var so = f.SchemaObject;
                var obj = env.Schema.Find(so.DatabaseIdentifier?.Value, so.SchemaIdentifier?.Value, so.BaseIdentifier.Value, o => o.Kind is ObjectKind.Tvf or ObjectKind.Func);
                var display = obj != null ? $"{obj.Schema}.{obj.Name}()" : so.BaseIdentifier.Value + "()";
                return new Source(alias ?? so.BaseIdentifier.Value, SourceKind.Tvf, display, () => Rename(SnapCols(obj), f.Columns)) { HasAlias = alias != null };
            }
            case PivotedTableReference p:
                return new Source(alias ?? "(pivot)", SourceKind.Pivot, "PIVOT", () => PivotCols(p, ctes)) { HasAlias = alias != null };
            case UnpivotedTableReference u:
                return new Source(alias ?? "(unpivot)", SourceKind.Pivot, "UNPIVOT", () => UnpivotCols(u, ctes)) { HasAlias = alias != null };
            case OpenJsonTableReference oj:
                return new Source(alias ?? "OPENJSON", SourceKind.Function, "OPENJSON", () =>
                    oj.SchemaDeclarationItems is { Count: > 0 } items
                        ? items.Select(i => new Col(i.ColumnDefinition.ColumnIdentifier.Value, TypeNames.Of(i.ColumnDefinition.DataType))).ToList()
                        : [new Col("key", "nvarchar(4000)"), new Col("value", "nvarchar(max)"), new Col("type", "int")]) { HasAlias = alias != null };
            case GlobalFunctionTableReference g:
            {
                var fn = g.Name.Value.ToUpperInvariant();
                IReadOnlyList<Col> cols = fn switch
                {
                    "STRING_SPLIT" => [new Col("value", "nvarchar(4000)"), new Col("ordinal", "bigint")],
                    "GENERATE_SERIES" => [new Col("value", g.Parameters.Count > 0 ? TypeOf(g.Parameters[0], [], ctes) ?? "int" : "int")],
                    _ => [],
                };
                return new Source(alias ?? g.Name.Value, SourceKind.Function, fn, () => cols) { HasAlias = alias != null };
            }
            default:
                return alias != null ? new Source(alias, SourceKind.Unknown, alias, () => []) { HasAlias = true } : null;
        }
    }

    /// <summary>A named rowset: CTE, temp table, trigger pseudo-table, then the snapshot (dbo first, then any schema).</summary>
    public Source Named(string? db, string? schema, string name, string? alias, CteScope ctes)
    {
        var exposed = alias ?? name;
        var hasAlias = alias != null;
        if (db == null && schema == null)
        {
            if (name.StartsWith('#'))
            {
                env.Tables.TryGetValue(name, out var t);
                return new Source(exposed, SourceKind.TempTable, "temp table " + name, () => t?.Columns ?? []) { HasAlias = hasAlias };
            }
            if (name.StartsWith('@'))
            {
                env.Tables.TryGetValue(name, out var t);
                return new Source(exposed, SourceKind.TableVar, "table variable " + name, () => t?.Columns ?? []) { HasAlias = hasAlias };
            }
            if (ctes.Find(name) is { } hit)
            {
                var (cte, index) = hit;
                return new Source(exposed, SourceKind.Cte, "CTE " + cte.ExpressionName.Value, () => CteColumns(cte, ctes.Upto(index))) { HasAlias = hasAlias };
            }
            if (env.Module?.TriggerTarget is { } target && (name.Equals("inserted", StringComparison.OrdinalIgnoreCase) || name.Equals("deleted", StringComparison.OrdinalIgnoreCase)))
            {
                var t = Named(target.DatabaseIdentifier?.Value, target.SchemaIdentifier?.Value, target.BaseIdentifier.Value, null, CteScope.Empty);
                return new Source(exposed, SourceKind.Pseudo, name.ToLowerInvariant() + " (" + t.Display + ")", () => t.Columns) { HasAlias = hasAlias };
            }
        }
        var obj = env.Schema.Find(db, schema, name, SchemaIndex.IsRowset);
        if (obj == null)
            return new Source(exposed, SourceKind.Unknown, Qualified(db, schema, name), () => []) { HasAlias = hasAlias };
        var kind = obj.Kind switch { ObjectKind.View => SourceKind.View, ObjectKind.Tvf => SourceKind.Tvf, ObjectKind.Synonym => SourceKind.Synonym, _ => SourceKind.Table };
        return new Source(exposed, kind, (db != null ? db + "." : "") + obj.Schema + "." + obj.Name, () => SnapCols(obj)) { HasAlias = hasAlias };
    }

    static string Qualified(string? db, string? schema, string name) =>
        string.Join(".", new[] { db, schema, name }.Where(x => x != null));

    public static IReadOnlyList<Col> SnapCols(SnapObject? o)
    {
        if (o?.Columns == null) return [];
        return o.Columns.Select(c =>
        {
            var notes = new List<string>();
            if (c.Pk) notes.Add("PK");
            if (c.Identity) notes.Add("identity");
            if (c.Computed) notes.Add("computed");
            notes.Add(c.Nullable ? "null" : "not null");
            return new Col(c.Name, c.Type, string.Join(", ", notes));
        }).ToList();
    }

    static IReadOnlyList<Col> Rename(IReadOnlyList<Col> cols, IList<Identifier>? names)
    {
        if (names == null || names.Count == 0) return cols;
        var list = new List<Col>(names.Count);
        for (var i = 0; i < names.Count; i++) list.Add(new Col(names[i].Value, i < cols.Count ? cols[i].Type : null, i < cols.Count ? cols[i].Note : null));
        return list;
    }

    public IReadOnlyList<Col> CteColumns(CommonTableExpression cte, CteScope visibleInside)
    {
        if (cteCols.TryGetValue(cte, out var hit)) return hit;
        cteCols[cte] = []; // recursion guard (recursive CTEs, cycles)
        var cols = Rename(Projection(cte.QueryExpression, visibleInside), cte.Columns);
        cteCols[cte] = cols;
        return cols;
    }

    public IReadOnlyList<Col> Projection(QueryExpression? q, CteScope ctes)
    {
        if (q == null || depth > 24) return [];
        depth++;
        try
        {
            switch (q)
            {
                case QueryParenthesisExpression p: return Projection(p.QueryExpression, ctes);
                case BinaryQueryExpression b: return Projection(b.FirstQueryExpression, ctes);
                case QuerySpecification qs:
                {
                    var sources = Flatten(qs.FromClause, ctes);
                    var cols = new List<Col>();
                    foreach (var el in qs.SelectElements)
                    {
                        switch (el)
                        {
                            case SelectScalarExpression sse:
                            {
                                var name = sse.ColumnName?.Value ?? (sse.Expression is ColumnReferenceExpression cr && cr.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids ? ids[^1].Value : null);
                                if (name == null) continue;
                                cols.Add(new Col(name, TypeOf(sse.Expression, sources, ctes)));
                                break;
                            }
                            case SelectStarExpression star:
                            {
                                if (star.Qualifier?.Identifiers is { Count: > 0 } qids)
                                {
                                    var s = FindSource(sources, qids[^1].Value);
                                    if (s != null) cols.AddRange(s.Columns);
                                }
                                else foreach (var s in sources) cols.AddRange(s.Columns);
                                break;
                            }
                        }
                    }
                    return cols;
                }
                default: return [];
            }
        }
        finally { depth--; }
    }

    public static Source? FindSource(IEnumerable<Source> sources, string exposed)
    {
        foreach (var s in sources) if (string.Equals(s.Exposed, exposed, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    IReadOnlyList<Col> PivotCols(PivotedTableReference p, CteScope ctes)
    {
        var inner = Flatten(p.TableReference, ctes).FirstOrDefault()?.Columns ?? [];
        var pivotCol = p.PivotColumn?.MultiPartIdentifier?.Identifiers is { Count: > 0 } pc ? pc[^1].Value : null;
        var valueCols = p.ValueColumns.Select(v => v.MultiPartIdentifier.Identifiers[^1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var valueType = inner.FirstOrDefault(c => valueCols.Contains(c.Name))?.Type;
        var agg = p.AggregateFunctionIdentifier?.Identifiers is { Count: > 0 } ai ? ai[^1].Value.ToUpperInvariant() : null;
        if (agg is "COUNT") valueType = "int";
        else if (agg is "COUNT_BIG") valueType = "bigint";
        var cols = inner.Where(c => !string.Equals(c.Name, pivotCol, StringComparison.OrdinalIgnoreCase) && !valueCols.Contains(c.Name)).ToList();
        cols.AddRange(p.InColumns.Select(i => new Col(i.Value, valueType, "pivoted")));
        return cols;
    }

    IReadOnlyList<Col> UnpivotCols(UnpivotedTableReference u, CteScope ctes)
    {
        var inner = Flatten(u.TableReference, ctes).FirstOrDefault()?.Columns ?? [];
        var inCols = u.InColumns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var valueType = inner.FirstOrDefault(c => inCols.Contains(c.Name))?.Type;
        var cols = inner.Where(c => !inCols.Contains(c.Name)).ToList();
        cols.Add(new Col(u.PivotColumn.Value, "nvarchar(128)", "unpivoted names"));
        cols.Add(new Col(u.ValueColumn.Value, valueType, "unpivoted values"));
        return cols;
    }

    // ---------------------------------------------------------------- type inference

    public string? TypeOf(ScalarExpression? e, IReadOnlyList<Source> scope, CteScope ctes)
    {
        if (e == null || depth > 32) return null;
        depth++;
        try { return Infer(e, scope, ctes); }
        catch { return null; }
        finally { depth--; }
    }

    string? Infer(ScalarExpression e, IReadOnlyList<Source> scope, CteScope ctes)
    {
        switch (e)
        {
            case ColumnReferenceExpression cr:
            {
                if (cr.ColumnType != ColumnType.Regular || cr.MultiPartIdentifier?.Identifiers is not { Count: > 0 } ids) return null;
                var col = ids[^1].Value;
                if (ids.Count >= 2)
                {
                    var s = FindSource(scope, ids[^2].Value);
                    return s?.Column(col)?.Type;
                }
                foreach (var s in scope) if (s.Column(col) is { } c) return c.Type;
                return null;
            }
            case CastCall c: return TypeNames.Of(c.DataType);
            case TryCastCall c: return TypeNames.Of(c.DataType);
            case ConvertCall c: return TypeNames.Of(c.DataType);
            case TryConvertCall c: return TypeNames.Of(c.DataType);
            case ParseCall c: return TypeNames.Of(c.DataType);
            case IntegerLiteral i: return long.TryParse(i.Value, out var v) && (v > int.MaxValue || v < int.MinValue) ? "bigint" : "int";
            case NumericLiteral n: return NumericType(n.Value);
            case RealLiteral: return "float";
            case MoneyLiteral: return "money";
            case StringLiteral s: return (s.IsNational ? "nvarchar(" : "varchar(") + Math.Max(1, s.Value?.Length ?? 1) + ")";
            case BinaryLiteral: return "varbinary";
            case NullLiteral: return null;
            case VariableReference vr: return env.Vars.TryGetValue(vr.Name, out var d) ? d.Type : null;
            case GlobalVariableExpression g: return Builtins.GlobalVariableType(g.Name);
            case ParenthesisExpression p: return TypeOf(p.Expression, scope, ctes);
            case UnaryExpression u: return TypeOf(u.Expression, scope, ctes);
            case IIfCall iif: return TypeOf(iif.ThenExpression, scope, ctes) ?? TypeOf(iif.ElseExpression, scope, ctes);
            case CoalesceExpression co:
                foreach (var x in co.Expressions) if (TypeOf(x, scope, ctes) is { } t) return t;
                return null;
            case NullIfExpression ni: return TypeOf(ni.FirstExpression, scope, ctes);
            case SearchedCaseExpression sc:
                foreach (var w in sc.WhenClauses) if (TypeOf(w.ThenExpression, scope, ctes) is { } t) return t;
                return TypeOf(sc.ElseExpression, scope, ctes);
            case SimpleCaseExpression sc:
                foreach (var w in sc.WhenClauses) if (TypeOf(w.ThenExpression, scope, ctes) is { } t) return t;
                return TypeOf(sc.ElseExpression, scope, ctes);
            case LeftFunctionCall l: return l.Parameters.Count > 0 ? TypeOf(l.Parameters[0], scope, ctes) : null;
            case RightFunctionCall r: return r.Parameters.Count > 0 ? TypeOf(r.Parameters[0], scope, ctes) : null;
            case ScalarSubquery sq: return Projection(sq.QueryExpression, ctes).FirstOrDefault()?.Type;
            case BinaryExpression b:
            {
                var l = TypeOf(b.FirstExpression, scope, ctes);
                var r = TypeOf(b.SecondExpression, scope, ctes);
                if (l == null) return r;
                if (r == null) return l;
                return TypeNames.Precedence(l) >= TypeNames.Precedence(r) ? l : r;
            }
            case FunctionCall fc:
            {
                if (fc.CallTarget is MultiPartIdentifierCallTarget mt && mt.MultiPartIdentifier?.Identifiers is { Count: > 0 } parts)
                {
                    var schema = parts[^1].Value;
                    var db = parts.Count >= 2 ? parts[^2].Value : null;
                    return env.Schema.Find(db, schema, fc.FunctionName.Value, o => o.Kind == ObjectKind.Func)?.ReturnType;
                }
                var args = fc.Parameters;
                string? Arg(int i) => i < args.Count ? TypeOf(args[i], scope, ctes) : null;
                return Builtins.ReturnType(fc.FunctionName.Value, Arg);
            }
            default: return null;
        }
    }

    static string NumericType(string? literal)
    {
        if (string.IsNullOrEmpty(literal)) return "numeric";
        var s = literal.TrimStart('-', '+');
        var dot = s.IndexOf('.');
        var digits = s.Count(char.IsDigit);
        var scale = dot >= 0 ? s.Length - dot - 1 : 0;
        return $"numeric({Math.Max(digits, 1)},{scale})";
    }
}

static class TypeNames
{
    public static string? Of(DataTypeReference? dt)
    {
        switch (dt)
        {
            case null: return null;
            case SqlDataTypeReference s:
            {
                var name = (s.Name?.BaseIdentifier?.Value ?? s.SqlDataTypeOption.ToString()).ToLowerInvariant();
                return name + Params(s.Parameters);
            }
            case XmlDataTypeReference: return "xml";
            case UserDataTypeReference u:
            {
                var n = u.Name;
                var name = n.SchemaIdentifier != null ? n.SchemaIdentifier.Value + "." + n.BaseIdentifier.Value : n.BaseIdentifier.Value;
                return name + Params(u.Parameters);
            }
            default: return dt.Name?.BaseIdentifier?.Value;
        }
    }

    static string Params(IList<Literal>? ps)
    {
        if (ps == null || ps.Count == 0) return "";
        return "(" + string.Join(",", ps.Select(p => p is MaxLiteral ? "max" : p.Value)) + ")";
    }

    // T-SQL data type precedence, highest first.
    static readonly string[] Order =
    [
        "sql_variant", "xml", "datetimeoffset", "datetime2", "datetime", "smalldatetime", "date", "time", "float", "real",
        "decimal", "numeric", "money", "smallmoney", "bigint", "int", "smallint", "tinyint", "bit", "ntext", "text", "image",
        "timestamp", "uniqueidentifier", "nvarchar", "nchar", "varchar", "char", "varbinary", "binary",
    ];

    public static int Precedence(string type)
    {
        var paren = type.IndexOf('(');
        var b = (paren >= 0 ? type[..paren] : type).Trim().ToLowerInvariant();
        var i = Array.IndexOf(Order, b);
        return i < 0 ? 0 : Order.Length - i;
    }
}
