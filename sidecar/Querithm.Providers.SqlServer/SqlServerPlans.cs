using System.Data.Common;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql;

namespace Querithm.Providers.SqlServer;

/// <summary>
/// Estimated plans via SET SHOWPLAN_XML (§11). ShowPlanXML → engine-neutral PlanNode. An actual plan is collected while
/// the query runs (query.execute with <c>actualPlan</c>, SET STATISTICS XML), so plan.get refuses the actual mode here.
/// </summary>
public sealed class SqlServerPlans : IPlanProvider
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public const string ActualRefusal =
        "On SQL Server the actual plan is collected while the query runs: turn on Include actual plan (or use Run with actual plan) and execute the query.";

    public const string PlanOptionRefusal =
        "The text changes the plan options itself (SET SHOWPLAN_XML / _ALL / _TEXT, SET STATISTICS XML / PROFILE), so the statements after it could really run instead of being planned. Remove those SET statements to get the estimated plan.";

    /// <summary>
    /// SHOWPLAN_XML ON compiles every later batch without running it; a SET of a showplan or statistics option in the text
    /// would switch that off (or on) for the batches after it, which would then execute. Found on the ScriptDom AST
    /// (<see cref="PredicateSetStatement"/>, <see cref="SetStatisticsStatement"/>); text that does not parse is refused when
    /// any SET token is in it, since its statements cannot be told apart.
    /// </summary>
    public string? Refusal(string sql, PlanMode mode)
    {
        if (mode == PlanMode.Actual) return ActualRefusal;
        var parsed = TSqlParsing.Parse(sql);
        var v = new PlanOptionVisitor();
        parsed.Script?.Accept(v);
        if (v.Found) return PlanOptionRefusal;
        if (parsed.Errors.Count > 0 && parsed.Tokens.Any(t => t.TokenType == TSqlTokenType.Set))
            return "The text has syntax errors and SET statements, so Querithm cannot check that none of them turns SHOWPLAN off (which would run the statements after it). Fix the syntax errors first.";
        return null;
    }

    /// <summary>SET SHOWPLAN_XML / _ALL / _TEXT and SET STATISTICS XML / PROFILE (IO and TIME only add messages).</summary>
    sealed class PlanOptionVisitor : TSqlFragmentVisitor
    {
        const SetOptions Showplan = SetOptions.ShowPlanAll | SetOptions.ShowPlanText | SetOptions.ShowPlanXml;
        const SetStatisticsOptions PlanStatistics = SetStatisticsOptions.Xml | SetStatisticsOptions.Profile;
        public bool Found { get; private set; }
        public override void Visit(SetStatisticsStatement node) => Found |= (node.Options & PlanStatistics) != 0;
        public override void Visit(PredicateSetStatement node) => Found |= (node.Options & Showplan) != 0;
    }

    public async Task<PlanResult> GetPlanAsync(DbConnection c, string sql, PlanMode mode, CancellationToken ct)
    {
        // Core asks first; checked again here so no caller can reach a run without SHOWPLAN on.
        if (Refusal(sql, mode) is { } refusal) throw new NotSupportedException(refusal);
        var batches = TSqlParsing.Parse(sql).Batches;
        var plans = new List<string>();
        await Exec(c, "SET SHOWPLAN_XML ON", ct).ConfigureAwait(false);
        try
        {
            foreach (var b in batches)
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = sql.Substring(b.StartOffset, b.Length);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                do
                {
                    if (r.FieldCount == 1 && r.GetName(0) == "Microsoft SQL Server 2005 XML Showplan")
                        while (await r.ReadAsync(ct).ConfigureAwait(false)) plans.Add(r.GetString(0));
                    else
                        while (await r.ReadAsync(ct).ConfigureAwait(false)) { /* nothing else comes back while SHOWPLAN is on */ }
                } while (await r.NextResultAsync(ct).ConfigureAwait(false));
            }
        }
        finally
        {
            // A connection left with SHOWPLAN on would plan, not run, its next borrower's statements: it is closed (and the
            // pool discards it) when OFF does not go through.
            using var limit = Cleanup.Start();
            try { await Exec(c, "SET SHOWPLAN_XML OFF", limit.Token).ConfigureAwait(false); }
            catch
            {
                try { c.Close(); } catch { /* discarded either way */ }
                throw;
            }
        }
        var statements = plans.SelectMany(p => Parse(p).Statements).ToArray();
        return new PlanResult { Plan = new PlanTree { Statements = statements }, Raw = string.Join("\n", plans), Format = "xml" };
    }

    static async Task Exec(DbConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public PlanTree Parse(string raw)
    {
        var doc = XDocument.Parse(raw);
        var statements = new List<PlanStatement>();
        foreach (var stmt in doc.Descendants(Ns + "StmtSimple").Concat(doc.Descendants(Ns + "StmtCursor")))
        {
            var qp = stmt.Element(Ns + "QueryPlan");
            var rootOp = qp?.Element(Ns + "RelOp");
            var stmtCost = D(stmt.Attribute("StatementSubTreeCost")) ?? (rootOp != null ? D(rootOp.Attribute("EstimatedTotalSubtreeCost")) : null) ?? 0;
            var warnings = new List<string>();
            if (qp?.Element(Ns + "Warnings") is { } w) warnings.AddRange(DescribeWarnings(w).Select(x => x.Message));
            var times = qp?.Element(Ns + "QueryTimeStats");
            statements.Add(new PlanStatement
            {
                Text = (string?)stmt.Attribute("StatementText"),
                SubtreeCost = stmtCost,
                Root = rootOp != null ? Node(rootOp, stmtCost) : null,
                MissingIndexes = qp == null ? [] : MissingIndexes(qp).ToArray(),
                Warnings = warnings.ToArray(),
                ElapsedMs = D(times?.Attribute("ElapsedTime")),
                CpuMs = D(times?.Attribute("CpuTime")),
            });
        }
        return new PlanTree { Statements = statements.ToArray() };
    }

    static double? D(XAttribute? a) => a != null && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Child RelOps are the nearest RelOp descendants (they sit under operator-specific elements).</summary>
    static IEnumerable<XElement> ChildRelOps(XElement relOp)
    {
        var stack = new Stack<XElement>(relOp.Elements().Reverse());
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.Name == Ns + "RelOp") { yield return e; continue; }
            foreach (var c in e.Elements().Reverse()) stack.Push(c);
        }
    }

    static PlanNode Node(XElement op, double statementCost)
    {
        var children = ChildRelOps(op).Select(c => Node(c, statementCost)).ToArray();
        var subtree = D(op.Attribute("EstimatedTotalSubtreeCost")) ?? 0;
        var own = Math.Max(0, subtree - children.Sum(c => c.SubtreeCost));
        var props = new Dictionary<string, string>();
        foreach (var a in op.Attributes()) props[a.Name.LocalName] = a.Value;

        // Operator-specific element (IndexScan, NestedLoops, Hash, ...) carries the object, predicates and warnings.
        var detail = op.Elements().FirstOrDefault(e => e.Name.LocalName is not ("OutputList" or "RunTimeInformation" or "Warnings" or "MemoryFractions" or "RunTimePartitionSummary" or "InternalInfo"));
        string? objectName = null;
        var obj = detail?.Elements(Ns + "Object").FirstOrDefault();
        if (obj != null)
        {
            var parts = new[] { "Database", "Schema", "Table", "Index" }.Select(n => ((string?)obj.Attribute(n))?.Trim('[', ']')).Where(s => !string.IsNullOrEmpty(s));
            objectName = string.Join(".", parts.Skip(obj.Attribute("Database") != null ? 1 : 0));
            props["Object"] = string.Join(".", parts);
        }
        if (detail != null)
        {
            foreach (var a in detail.Attributes()) props[$"{detail.Name.LocalName}.{a.Name.LocalName}"] = a.Value;
            var seek = detail.Descendants(Ns + "SeekPredicates").FirstOrDefault()?.Descendants(Ns + "ScalarOperator").Select(s => (string?)s.Attribute("ScalarString")).FirstOrDefault(s => s != null);
            if (seek != null) props["Seek Predicate"] = seek;
            var pred = detail.Elements(Ns + "Predicate").FirstOrDefault()?.Element(Ns + "ScalarOperator")?.Attribute("ScalarString")?.Value;
            if (pred != null) props["Predicate"] = pred;
        }
        var output = op.Element(Ns + "OutputList")?.Elements(Ns + "ColumnReference")
            .Select(c => string.Join(".", new[] { (string?)c.Attribute("Alias") ?? (string?)c.Attribute("Table"), (string?)c.Attribute("Column") }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.Trim('[', ']'))))
            .ToList();
        if (output is { Count: > 0 }) props["Output List"] = string.Join(", ", output);

        double? actualRows = null, actualExec = null, elapsed = null;
        long? reads = null;
        var counters = op.Element(Ns + "RunTimeInformation")?.Elements(Ns + "RunTimeCountersPerThread").ToList();
        if (counters is { Count: > 0 })
        {
            actualRows = counters.Sum(c => D(c.Attribute("ActualRows")) ?? 0);
            actualExec = counters.Sum(c => D(c.Attribute("ActualExecutions")) ?? 0);
            if (counters.Any(c => c.Attribute("ActualElapsedms") != null)) elapsed = counters.Max(c => D(c.Attribute("ActualElapsedms")) ?? 0);
            if (counters.Any(c => c.Attribute("ActualLogicalReads") != null)) reads = (long)counters.Sum(c => D(c.Attribute("ActualLogicalReads")) ?? 0);
        }

        var warnings = new List<PlanWarning>();
        foreach (var w in op.Elements(Ns + "Warnings").Concat(detail?.Elements(Ns + "Warnings") ?? [])) warnings.AddRange(DescribeWarnings(w));
        var est = D(op.Attribute("EstimateRows")) ?? 0;
        var execs = D(op.Attribute("EstimateRewinds")) + D(op.Attribute("EstimateRebinds")) + 1;
        var estTotal = est * (execs ?? 1);
        if (actualRows is { } ar && estTotal > 0 && (ar / estTotal > 10 || (ar > 0 && estTotal / ar > 10)))
            warnings.Add(new PlanWarning { Kind = "cardinality", Message = $"Actual rows {ar:N0} vs estimated {estTotal:N0} ({Math.Max(ar / estTotal, estTotal / Math.Max(ar, 1)):N0}x skew)." });

        return new PlanNode
        {
            Id = (string?)op.Attribute("NodeId") ?? Guid.NewGuid().ToString("N"),
            Operator = PrettyOperator(op, detail),
            PhysicalOp = (string?)op.Attribute("PhysicalOp") ?? "",
            LogicalOp = (string?)op.Attribute("LogicalOp"),
            ObjectName = objectName,
            EstRows = est,
            ActualRows = actualRows,
            ActualExecutions = actualExec,
            EstCost = own,
            SubtreeCost = subtree,
            CostPercent = statementCost > 0 ? Math.Round(own / statementCost * 100, 1) : null,
            ElapsedMs = elapsed,
            LogicalReads = reads,
            Warnings = warnings.ToArray(),
            Properties = props,
            Children = children,
        };
    }

    static string PrettyOperator(XElement op, XElement? detail)
    {
        var physical = (string?)op.Attribute("PhysicalOp") ?? "Operator";
        var logical = (string?)op.Attribute("LogicalOp");
        if (physical is "Nested Loops" or "Hash Match" or "Merge Join" && logical != null && logical != physical) return $"{physical} ({logical})";
        if (physical == "Clustered Index Seek" && detail?.Attribute("Lookup")?.Value is "1" or "true") return "Key Lookup (Clustered)";
        return physical;
    }

    static IEnumerable<PlanWarning> DescribeWarnings(XElement w)
    {
        foreach (var e in w.Elements())
        {
            var name = e.Name.LocalName;
            yield return name switch
            {
                "PlanAffectingConvert" => new PlanWarning { Kind = "implicitConversion", Message = $"Implicit conversion ({e.Attribute("ConvertIssue")?.Value}): {e.Attribute("Expression")?.Value}" },
                "SpillToTempDb" => new PlanWarning { Kind = "spill", Message = $"Spill to tempdb (level {e.Attribute("SpillLevel")?.Value})" },
                "HashSpillDetails" or "SortSpillDetails" or "ExchangeSpillDetails" => new PlanWarning { Kind = "spill", Message = $"{name.Replace("Details", "")}: {e.Attribute("WritesToTempDb")?.Value} pages written to tempdb" },
                "NoJoinPredicate" => new PlanWarning { Kind = "noJoinPredicate", Message = "No join predicate" },
                "ColumnsWithNoStatistics" => new PlanWarning { Kind = "other", Message = "Columns with no statistics: " + string.Join(", ", e.Descendants(Ns + "ColumnReference").Select(c => (string?)c.Attribute("Column"))) },
                "MemoryGrantWarning" => new PlanWarning { Kind = "other", Message = $"Memory grant warning: {e.Attribute("GrantWarningKind")?.Value}" },
                _ => new PlanWarning { Kind = "other", Message = name },
            };
        }
        foreach (var a in w.Attributes())
            if (a.Value is "1" or "true") yield return new PlanWarning { Kind = a.Name.LocalName == "NoJoinPredicate" ? "noJoinPredicate" : "other", Message = a.Name.LocalName };
    }

    static IEnumerable<MissingIndex> MissingIndexes(XElement queryPlan)
    {
        foreach (var group in queryPlan.Descendants(Ns + "MissingIndexGroup"))
        {
            var impact = D(group.Attribute("Impact")) ?? 0;
            foreach (var mi in group.Elements(Ns + "MissingIndex"))
            {
                string Clean(string? s) => (s ?? "").Trim('[', ']');
                var table = $"{Clean((string?)mi.Attribute("Schema"))}.{Clean((string?)mi.Attribute("Table"))}";
                string[] Cols(string usage) => mi.Elements(Ns + "ColumnGroup").Where(g => (string?)g.Attribute("Usage") == usage)
                    .SelectMany(g => g.Elements(Ns + "Column")).Select(c => Clean((string?)c.Attribute("Name"))).ToArray();
                var eq = Cols("EQUALITY");
                var ineq = Cols("INEQUALITY");
                var inc = Cols("INCLUDE");
                var keys = eq.Concat(ineq).ToArray();
                var name = $"IX_{Clean((string?)mi.Attribute("Table"))}_{string.Join("_", keys)}";
                if (name.Length > 120) name = name[..120];
                var create = $"CREATE NONCLUSTERED INDEX [{name}]\nON [{Clean((string?)mi.Attribute("Schema"))}].[{Clean((string?)mi.Attribute("Table"))}] ({string.Join(", ", keys.Select(k => $"[{k}]"))})"
                             + (inc.Length > 0 ? $"\nINCLUDE ({string.Join(", ", inc.Select(k => $"[{k}]"))})" : "") + ";";
                yield return new MissingIndex { Impact = impact, Table = table, EqualityColumns = eq, InequalityColumns = ineq, IncludeColumns = inc, CreateStatement = create };
            }
        }
    }
}
