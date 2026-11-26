using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;

namespace Querithm.Intelligence.TSql;

/// <summary>
/// Destructive-statement and DDL classification on the ScriptDom AST (§6 Environment safety). Not regex.
/// Destructive = DELETE/UPDATE without WHERE, DROP, TRUNCATE, ALTER (including CREATE OR ALTER), MERGE with a DELETE
/// action, and dynamic SQL: <c>EXEC('…')</c> / <c>sp_executesql N'…'</c> of literal text is parsed and classified like the
/// script (its findings say they run as dynamic SQL); text built at run time (<c>EXEC (@sql)</c>, a variable or an
/// expression) cannot be checked, so it counts as destructive (rule <c>dynamicSql</c>).
/// </summary>
public static class Classifier
{
    /// <summary>Literal dynamic SQL inside literal dynamic SQL is classified this deep; deeper counts as unknown.</summary>
    const int MaxDynamicDepth = 4;

    public static Classification Classify(ParsedScript parsed) => Classify(parsed, 0);

    static Classification Classify(ParsedScript parsed, int depth)
    {
        var v = new Visitor(parsed.Text, depth);
        parsed.Script?.Accept(v);
        return new Classification
        {
            Destructive = v.Findings.ToArray(),
            HasDdl = v.HasDdl,
            BeginsTransaction = v.Begins,
            EndsTransaction = v.Ends,
            UseDatabase = v.Use,
            Batches = parsed.Batches,
            ParseErrors = parsed.Errors.Select(e => new Diagnostic
            {
                Line = e.Line, Column = e.Column, EndLine = e.Line, EndColumn = e.Column + 1, Message = e.Message, Severity = Severity.Error,
            }).ToArray(),
        };
    }

    sealed class Visitor(string text, int depth) : TSqlFragmentVisitor
    {
        public List<DestructiveFinding> Findings { get; } = [];
        public bool HasDdl { get; private set; }
        public bool Begins { get; private set; }
        public bool Ends { get; private set; }
        public string? Use { get; private set; }

        void Add(TSqlFragment f, string rule, string description) => Findings.Add(new DestructiveFinding
        {
            Rule = rule, Statement = TSqlParsing.Snippet(text, f), Line = f.StartLine, Column = f.StartColumn, Description = description,
        });

        // Statements inside a module body (procedure/function/trigger) run later, not now: only the module statement counts.
        int _module;

        void InModule(TSqlFragment node, Action visitBase)
        {
            Visit((TSqlStatement)node);
            _module++;
            try { visitBase(); } finally { _module--; }
        }

        public override void ExplicitVisit(CreateProcedureStatement node) => InModule(node, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(AlterProcedureStatement node) => InModule(node, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => InModule(node, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(CreateFunctionStatement node) => InModule(node, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(AlterFunctionStatement node) => InModule(node, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => InModule(node, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(CreateTriggerStatement node) => InModule(node, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(AlterTriggerStatement node) => InModule(node, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => InModule(node, () => base.ExplicitVisit(node));

        readonly HashSet<TSqlStatement> _seen = new(ReferenceEqualityComparer.Instance);

        public override void Visit(TSqlStatement node)
        {
            if (_module > 0 || !_seen.Add(node)) return;
            var name = node.GetType().Name;
            if (node is TruncateTableStatement)
            {
                Add(node, "truncate", "TRUNCATE TABLE removes every row.");
                return;
            }
            // RESTORE DATABASE / LOG overwrites the database it restores into (round-3 review 2026-11-26: not classified).
            if (node is RestoreStatement { Kind: RestoreStatementKind.Database or RestoreStatementKind.TransactionLog })
            {
                Add(node, "restore", "RESTORE overwrites the database with the backup's contents.");
                return;
            }
            if (name.StartsWith("Drop", StringComparison.Ordinal))
            {
                HasDdl = true;
                Add(node, "drop", $"{Words(name)} permanently removes the object.");
                return;
            }
            if (name.StartsWith("Alter", StringComparison.Ordinal) || name.StartsWith("CreateOrAlter", StringComparison.Ordinal))
            {
                HasDdl = true;
                Add(node, "alter", $"{Words(name)} changes an existing object.");
                return;
            }
            if (name.StartsWith("Create", StringComparison.Ordinal)) HasDdl = true;
            if (node is BeginTransactionStatement) Begins = true;
            if (node is CommitTransactionStatement or RollbackTransactionStatement) Ends = true;
            if (node is UseStatement use) Use = use.DatabaseName?.Value;
        }

        public override void Visit(DeleteSpecification node)
        {
            if (_module == 0 && node.WhereClause == null) Add(node, "deleteWithoutWhere", "DELETE without a WHERE clause removes every row.");
        }

        public override void Visit(UpdateSpecification node)
        {
            if (_module == 0 && node.WhereClause == null) Add(node, "updateWithoutWhere", "UPDATE without a WHERE clause changes every row.");
        }

        public override void Visit(MergeSpecification node)
        {
            if (_module == 0 && node.ActionClauses.Any(a => a.Action is DeleteMergeAction))
                Add(node, "mergeDelete", "MERGE … THEN DELETE removes every row its WHEN clause matches (NOT MATCHED BY SOURCE: every target row the source lacks).");
        }

        /// <summary>EXEC('…'), EXEC (@sql), sp_executesql — also as the source of INSERT … EXEC.</summary>
        public override void Visit(ExecuteSpecification node)
        {
            if (_module > 0) return;
            switch (node.ExecutableEntity)
            {
                case ExecutableStringList list:
                    Dynamic(node, list.Strings.Count > 0 && list.Strings.All(x => x is StringLiteral) ? string.Concat(list.Strings.Cast<StringLiteral>().Select(x => x.Value)) : null, "EXEC");
                    break;
                case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var name } proc
                    when string.Equals(name, "sp_executesql", StringComparison.OrdinalIgnoreCase):
                    // The statement is the first parameter (positional, or @stmt =).
                    Dynamic(node, Argument(proc, "@stmt", 0)?.ParameterValue is StringLiteral literal ? literal.Value : null, "sp_executesql");
                    break;
                // sp_sqlexec (the legacy form of the same): its text is the first parameter.
                case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var legacy } sqlexec
                    when string.Equals(legacy, "sp_sqlexec", StringComparison.OrdinalIgnoreCase):
                    Dynamic(node, sqlexec.Parameters.FirstOrDefault()?.ParameterValue is StringLiteral text ? text.Value : null, "sp_sqlexec");
                    break;
                // The undocumented loops run their command text once per table / database with "?" replaced (8a review): it is
                // a template, not a statement, so it cannot be checked.
                case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var each }
                    when string.Equals(each, "sp_MSforeachtable", StringComparison.OrdinalIgnoreCase) || string.Equals(each, "sp_MSforeachdb", StringComparison.OrdinalIgnoreCase):
                    Add(node, "dynamicSql", $"{each} runs its command text once for every {(each.EndsWith("db", StringComparison.OrdinalIgnoreCase) ? "database" : "table")}, which cannot be checked for destructive statements.");
                    break;
                // The RPC-level procedures that prepare or run statement text (round-3 review 2026-11-26: sp_prepexec … ran
                // a DROP with no finding). The text is classified where it is a literal at its documented position.
                case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var rpc } prep
                    when StatementPosition(rpc) is { } at:
                    Dynamic(node, Argument(prep, "@stmt", at)?.ParameterValue is StringLiteral prepared ? prepared.Value : null, rpc);
                    break;
                // Procedures that run text of their own or a handle prepared earlier: nothing here to check.
                case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var runs }
                    when RunsUncheckedText(runs):
                    Add(node, "dynamicSql", $"{runs} runs text (a prepared statement, a cursor, a command line or a script) that cannot be checked for destructive statements.");
                    break;
                case ExecutableProcedureReference { ProcedureReference.ProcedureVariable: not null }:
                    Add(node, "dynamicSql", "EXEC @name runs a procedure whose name is only known at run time, so it cannot be checked for destructive statements.");
                    break;
            }
        }

        /// <summary>The 0-based position of the statement text of the procedures that take one (sp_prepexec, sp_prepare, the cursor procedures).</summary>
        static int? StatementPosition(string name) => name.ToUpperInvariant() switch
        {
            "SP_PREPEXEC" or "SP_PREPARE" or "SP_CURSORPREPARE" => 2,
            "SP_CURSORPREPEXEC" => 3,
            "SP_CURSOROPEN" => 1,
            _ => null,
        };

        static bool RunsUncheckedText(string name) => name.ToUpperInvariant() is "SP_EXECUTE" or "SP_CURSOREXECUTE" or "SP_PREPEXECRPC"
            or "XP_CMDSHELL" or "SP_EXECUTE_EXTERNAL_SCRIPT" or "SP_CURSOR";

        /// <summary>
        /// A procedure's argument at <paramref name="position"/> when the arguments up to it are positional (T-SQL puts
        /// positional arguments first), else the one named <paramref name="name"/>; null for none or a name given twice
        /// (the caller then counts the text as unknown). Gemini review 2026-11-26: the name was looked up first, so in
        /// <c>sp_executesql N'DROP …', N'@stmt nvarchar(20)', @stmt = N'SELECT 1'</c> — the dynamic query's own parameter
        /// named @stmt — the SELECT was classified and the DROP ran.
        /// </summary>
        static ExecuteParameter? Argument(ExecutableProcedureReference proc, string name, int position)
        {
            var ps = proc.Parameters;
            if (position < ps.Count && ps.Take(position + 1).All(x => x.Variable == null)) return ps[position];
            ExecuteParameter? match = null;
            foreach (var p in ps)
                if (p.Variable?.Name is { } n && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (match != null) return null;
                    match = p;
                }
            return match;
        }

        /// <summary>OPENQUERY / OPENROWSET pass their text to another server, which runs it as it is (round-3 review 2026-11-26).</summary>
        public override void Visit(OpenQueryTableReference node)
        {
            if (_module == 0) Add(node, "dynamicSql", "OPENQUERY runs its text on a linked server, which cannot be checked for destructive statements.");
        }

        public override void Visit(OpenRowsetTableReference node)
        {
            if (_module == 0 && node.Query != null) Add(node, "dynamicSql", "OPENROWSET runs its query text on another server, which cannot be checked for destructive statements.");
        }

        /// <summary>Literal text is classified as a script of its own; text built at run time (or nested too deep) is unknown.</summary>
        void Dynamic(TSqlFragment node, string? literal, string how)
        {
            if (literal == null || depth >= MaxDynamicDepth)
            {
                Add(node, "dynamicSql", $"{how} runs SQL text built at run time, which cannot be checked for destructive statements.");
                return;
            }
            var inner = Classify(TSqlParsing.ParseUncached(literal), depth + 1);
            HasDdl |= inner.HasDdl;
            if (inner.ParseErrors.Length > 0 && inner.Destructive.Length == 0)
                Add(node, "dynamicSql", $"The SQL text {how} runs could not be parsed, so it cannot be checked for destructive statements.");
            foreach (var f in inner.Destructive)
                Findings.Add(f with { Line = node.StartLine, Column = node.StartColumn, Description = $"{f.Description} (run by {how} as dynamic SQL)" });
        }

        static string Words(string typeName)
        {
            var n = typeName.EndsWith("Statement", StringComparison.Ordinal) ? typeName[..^"Statement".Length] : typeName;
            var sb = new System.Text.StringBuilder();
            foreach (var ch in n)
            {
                if (char.IsUpper(ch) && sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }
    }
}
