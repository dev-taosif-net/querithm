using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;

namespace Querithm.Intelligence.TSql;

/// <summary>
/// lang.format with ScriptDom's script generator (§10). The generator drops comments, so statements that
/// contain comments are kept verbatim and the text between statements (comments, blank lines) is preserved.
/// </summary>
public static class Formatting
{
    public static FormatResult Format(string text, FormatOptions? options)
    {
        var parsed = TSqlParsing.Parse(text);
        if (parsed.Errors.Count > 0 || parsed.Script == null)
            return new FormatResult { Text = text, Error = $"Cannot format: line {parsed.Errors[0].Line}: {parsed.Errors[0].Message}" };

        var o = options ?? new FormatOptions();
        var gen = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = o.KeywordCase switch { KeywordCase.Lower => KeywordCasing.Lowercase, KeywordCase.Preserve => KeywordCasing.PascalCase, _ => KeywordCasing.Uppercase },
            IndentationSize = o.IndentSize ?? 4,
            NewLineBeforeJoinClause = o.NewlineBeforeJoin ?? true,
            AlignClauseBodies = false,
            IncludeSemicolons = true,
            SqlVersion = SqlVersion.Sql160,
        });

        var sb = new StringBuilder();
        var cursor = 0;
        var tokens = parsed.Tokens;
        for (var b = 0; b < parsed.Script.Batches.Count; b++)
        {
            var batch = parsed.Script.Batches[b];
            foreach (var stmt in batch.Statements)
            {
                // Preserve everything between statements exactly (comments, blank lines).
                if (stmt.StartOffset > cursor) sb.Append(Gap(text, cursor, stmt.StartOffset));
                var hasComment = false;
                for (var i = stmt.FirstTokenIndex; i <= stmt.LastTokenIndex && i < tokens.Count; i++)
                    if (tokens[i].TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment) { hasComment = true; break; }
                var hadSemicolon = stmt.LastTokenIndex >= 0 && stmt.LastTokenIndex < tokens.Count && tokens[stmt.LastTokenIndex].TokenType == TSqlTokenType.Semicolon;
                cursor = stmt.StartOffset + stmt.FragmentLength;
                // Keep a trailing semicolon that ScriptDom left outside the fragment.
                if (cursor < text.Length && text[cursor] == ';') { hadSemicolon = true; cursor++; }
                if (hasComment)
                {
                    sb.Append(text, stmt.StartOffset, stmt.FragmentLength);
                    if (hadSemicolon && (sb.Length == 0 || sb[^1] != ';')) sb.Append(';');
                }
                else
                {
                    gen.GenerateScript(stmt, out var formatted);
                    var tidied = Tidy(formatted, o.LeadingCommas);
                    sb.Append(tidied);
                    if ((hadSemicolon || stmt is MergeStatement) && !tidied.EndsWith(';')) sb.Append(';');
                }
            }
            var batchEnd = batch.StartOffset + batch.FragmentLength;
            if (batchEnd > cursor) { sb.Append(Gap(text, cursor, batchEnd)); cursor = batchEnd; }
        }
        if (cursor < text.Length) sb.Append(text, cursor, text.Length - cursor);
        return new FormatResult { Text = sb.ToString() };
    }

    static string Gap(string text, int from, int to)
    {
        var gap = text[from..to];
        // Collapse runs of blank lines to one (at most two newlines in a row), keep comments and GO separators verbatim.
        // Counting newlines, not split entries: a gap can start mid-line (after ';') and ends where the statement's line begins.
        var trimmed = string.Join("\n", gap.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()));
        var sb = new StringBuilder();
        var newlines = 0;
        foreach (var ch in trimmed)
        {
            if (ch != '\n') newlines = 0;
            else if (++newlines > 2) continue;
            sb.Append(ch);
        }
        var result = sb.ToString();
        if (!result.Contains('\n') && result.Trim().Length == 0) return gap.Contains('\n') ? "\n" : " ";
        return result;
    }

    /// <summary>
    /// Post-processes generator output. ScriptDom ends lines with Environment.NewLine (CRLF on Windows); normalise to
    /// '\n' like Gap() so the result has one line ending and LeadingCommas sees the commas at line ends.
    /// </summary>
    internal static string Tidy(string generated, bool leadingCommas)
    {
        var s = generated.Replace("\r\n", "\n").TrimEnd();
        return leadingCommas ? LeadingCommas(s) : s;
    }

    /// <summary>Moves trailing commas at line ends to the start of the next line.</summary>
    static string LeadingCommas(string s)
    {
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i].EndsWith(','))
            {
                lines[i] = lines[i][..^1];
                var next = lines[i + 1];
                var indent = next.Length - next.TrimStart().Length;
                lines[i + 1] = next[..indent] + ", " + next.TrimStart();
            }
        }
        return string.Join("\n", lines);
    }
}

/// <summary>Folding ranges from the AST (§9): BEGIN/END, CTE bodies, CASE, multi-line parentheses, modules, block comments.</summary>
public static class Folding
{
    public static FoldingRange[] Ranges(ParsedScript parsed)
    {
        var list = new List<FoldingRange>();
        if (parsed.Script != null)
        {
            var v = new Visitor(list);
            parsed.Script.Accept(v);
        }
        foreach (var t in parsed.Tokens)
        {
            if (t.TokenType == TSqlTokenType.MultilineComment)
            {
                var lines = t.Text.Count(c => c == '\n');
                if (lines > 0) list.Add(new FoldingRange { StartLine = t.Line, EndLine = t.Line + lines, Kind = "comment" });
            }
        }
        // Explicit regions: --region ... --endregion
        var stack = new Stack<int>();
        foreach (var t in parsed.Tokens.Where(t => t.TokenType == TSqlTokenType.SingleLineComment))
        {
            var body = t.Text.TrimStart('-').Trim();
            if (body.StartsWith("region", StringComparison.OrdinalIgnoreCase)) stack.Push(t.Line);
            else if (body.StartsWith("endregion", StringComparison.OrdinalIgnoreCase) && stack.Count > 0)
                list.Add(new FoldingRange { StartLine = stack.Pop(), EndLine = t.Line, Kind = "region" });
        }
        return list.Where(r => r.EndLine > r.StartLine)
            .GroupBy(r => (r.StartLine, r.EndLine)).Select(g => g.First())
            .OrderBy(r => r.StartLine).ThenByDescending(r => r.EndLine).ToArray();
    }

    sealed class Visitor(List<FoldingRange> list) : TSqlFragmentVisitor
    {
        void Add(TSqlFragment f, string? kind = null)
        {
            if (f.StartLine <= 0) return;
            var end = TSqlParsing.EndLine(f);
            if (end > f.StartLine) list.Add(new FoldingRange { StartLine = f.StartLine, EndLine = end, Kind = kind });
        }

        public override void Visit(BeginEndBlockStatement node) => Add(node);
        public override void Visit(TryCatchStatement node) => Add(node);
        public override void Visit(CommonTableExpression node) => Add(node);
        public override void Visit(SearchedCaseExpression node) => Add(node);
        public override void Visit(SimpleCaseExpression node) => Add(node);
        public override void Visit(QueryDerivedTable node) => Add(node);
        public override void Visit(ScalarSubquery node) => Add(node);
        public override void Visit(ParenthesisExpression node) => Add(node);
        public override void Visit(QueryParenthesisExpression node) => Add(node);
        public override void Visit(IfStatement node) => Add(node);
        public override void Visit(WhileStatement node) => Add(node);
        public override void Visit(ProcedureStatementBody node) => Add(node);
        public override void Visit(FunctionStatementBody node) => Add(node);
        public override void Visit(ViewStatementBody node) => Add(node);
        public override void Visit(TriggerStatementBody node) => Add(node);
        public override void Visit(CreateTableStatement node) => Add(node);
        public override void Visit(MergeStatement node) => Add(node);
        public override void Visit(SelectStatement node) => Add(node);
        public override void Visit(InsertStatement node) => Add(node);
        public override void Visit(UpdateStatement node) => Add(node);
        public override void Visit(DeleteStatement node) => Add(node);
    }
}
