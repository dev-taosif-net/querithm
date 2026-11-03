using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querithm.Intelligence.TSql.Completion;

/// <summary>Where the cursor is: the partial word, its qualifier chain, the token before it and the enclosing statement.</summary>
sealed class CursorInfo
{
    public int Offset;
    /// <summary>[PrefixStart, PrefixEnd) is the word under the cursor (may extend past the cursor).</summary>
    public int PrefixStart, PrefixEnd;
    public string Prefix = "";
    /// <summary>Unquoted qualifier parts before the word: 'o' in o.Cu, ['SalesDB','dbo'] in SalesDB.dbo.Or.</summary>
    public List<string> Qualifier = [];
    public int ChainStart;
    /// <summary>Significant token index just before the qualifier chain, or -1.</summary>
    public int Prev = -1;
    public required Batch Batch;
    /// <summary>Enclosing statement, or null at the start of a new statement.</summary>
    public Segment? Seg;
    public int StmtStart;

    public static CursorInfo Compute(DocModel doc, int offset)
    {
        var s = doc.Text;
        offset = Math.Clamp(offset, 0, s.Length);
        var ps = offset;
        while (ps > 0 && SqlLexer.IsWordChar(s[ps - 1])) ps--;
        if (ps > 0 && s[ps - 1] == '[') ps--;
        var pe = offset;
        while (pe < s.Length && SqlLexer.IsWordChar(s[pe])) pe++;
        if (ps < s.Length && s[ps] == '[' && pe < s.Length && s[pe] == ']') pe++;
        var info = new CursorInfo { Offset = offset, PrefixStart = ps, PrefixEnd = pe, Prefix = s[ps..offset], Batch = doc.BatchAt(offset) };
        var chain = ps;
        while (chain > 0 && s[chain - 1] == '.')
        {
            var j = chain - 1;
            if (j > 0 && s[j - 1] == ']')
            {
                var k = s.LastIndexOf('[', j - 1);
                if (k < 0) break;
                info.Qualifier.Insert(0, s[(k + 1)..(j - 1)]);
                chain = k;
            }
            else if (j > 0 && s[j - 1] == '"')
            {
                var k = j - 2 >= 0 ? s.LastIndexOf('"', j - 2) : -1;
                if (k < 0) break;
                info.Qualifier.Insert(0, s[(k + 1)..(j - 1)]);
                chain = k;
            }
            else if (j > 0 && SqlLexer.IsWordChar(s[j - 1]))
            {
                var k = j;
                while (k > 0 && SqlLexer.IsWordChar(s[k - 1])) k--;
                info.Qualifier.Insert(0, s[k..j]);
                chain = k;
            }
            else
            {
                info.Qualifier.Insert(0, ""); // db..table
                chain = j;
            }
        }
        info.ChainStart = chain;
        info.Prev = doc.LastTokBefore(chain);
        var b = info.Batch;
        if (info.Prev >= b.FirstTok && info.Prev < b.EndTok)
        {
            var seg = FindSeg(doc, b, info.Prev);
            if (seg != null && seg.Kind != SegKind.Marker && !(seg.Kind == SegKind.Header && seg.HeaderClosed && info.Prev == seg.Last))
                info.Seg = seg;
        }
        info.StmtStart = info.Seg?.StartOffset ?? chain;
        return info;
    }

    static Segment? FindSeg(DocModel doc, Batch b, int tok)
    {
        int lo = b.FirstSeg, hi = b.EndSeg - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var g = doc.Segs[mid];
            if (tok < g.First) hi = mid - 1;
            else if (tok > g.Last) lo = mid + 1;
            else return g;
        }
        return null;
    }
}

/// <summary>The enclosing statement parsed with a placeholder identifier at the cursor.</summary>
sealed class StmtAst
{
    public required TSqlStatement Stmt;
    public required int PhRel;
    public required int PhLen;
    public required List<TSqlFragment> Chain;
}

static class StatementAnalysis
{
    public const string Placeholder = "__q__";

    static readonly HashSet<string> CutKw = ["WHERE", "GROUP", "HAVING", "ORDER", "OPTION", "UNION", "EXCEPT", "INTERSECT", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "WHEN", "OUTPUT"];

    /// <summary>Parses the statement around the cursor, trying progressively more aggressive repairs of the partial text.</summary>
    public static StmtAst? Parse(DocModel doc, CursorInfo cur)
    {
        var seg = cur.Seg;
        if (seg == null || seg.Kind != SegKind.Statement) return null;
        var s = doc.Text;
        var stmtEnd = Math.Max(seg.EndOffset, cur.PrefixEnd);
        if (cur.PrefixStart < seg.StartOffset) return null;
        var head = s[seg.StartOffset..cur.PrefixStart];
        var tail = s[cur.PrefixEnd..stmtEnd];
        var ph = cur.Prefix.StartsWith('@') ? "@" + Placeholder : cur.Prefix.StartsWith('#') ? "#" + Placeholder : Placeholder;
        // MERGE must be terminated by ';' — every variant ends with one.
        var extra = (seg.Kw is "IF" or "WHILE" ? "\nRETURN" : "") + ";";
        var closers = Closers(doc, seg.First, cur.Prev);
        var tried = new HashSet<string>();
        StmtAst? Try(string text)
        {
            if (!tried.Add(text)) return null;
            var parsed = StatementParser.Parse(text);
            if (!parsed.Ok || parsed.First is not { } stmt) return null;
            var chain = Chain(stmt, head.Length, ph.Length);
            return chain.Count == 0 ? null : new StmtAst { Stmt = stmt, PhRel = head.Length, PhLen = ph.Length, Chain = chain };
        }

        var full = head + ph + tail + extra;
        if (full.Length < 200_000)
        {
            if (Try(full) is { } a) return a;
            if (Try(head + ph + " = 1" + tail + extra) is { } b) return b;
            // Parentheses left open at the end of the statement: close them after the tail.
            var allClosers = Closers(doc, seg.First, seg.Last);
            if (allClosers.Length > 0)
            {
                if (Try(head + ph + tail + allClosers + extra) is { } e) return e;
                if (Try(head + ph + " = 1" + tail + allClosers + extra) is { } f) return f;
            }
            // Cut the statement at the clause before the first parse error that follows the cursor.
            var first = StatementParser.Parse(full);
            var errRel = first.FirstErrorOffset;
            var phEnd = head.Length + ph.Length;
            if (errRel >= phEnd)
            {
                var errDoc = cur.PrefixEnd + (errRel - phEnd);
                var cut = CutPoint(doc, seg, cur, errDoc);
                if (cut > 0)
                {
                    var mid = s[cur.PrefixEnd..doc.Toks[cut].Start];
                    var cl = Closers(doc, seg.First, cut - 1);
                    if (Try(head + ph + mid + cl + extra) is { } c) return c;
                    if (Try(head + ph + " = 1" + mid + cl + extra) is { } d) return d;
                }
            }
        }
        // Truncate at the cursor and close what is open.
        var withSel = seg.Kw == "WITH" ? " SELECT 1" : "";
        foreach (var v in new[] { ph + closers, ph + " = 1" + closers, ph + " ON 1=1" + closers, ph + closers + withSel, ph + " = 1" + closers + withSel })
            if (Try(head + v + extra) is { } r) return r;
        return null;
    }

    /// <summary>Text closing every '(' and CASE still open between token indices [from, to].</summary>
    static string Closers(DocModel doc, int from, int to)
    {
        var stack = new List<(char Kind, bool WhenOpen)>();
        for (var i = from; i <= to && i < doc.Toks.Count; i++)
        {
            if (doc.IsP(i, '(')) stack.Add(('(', false));
            else if (doc.IsP(i, ')')) { if (stack.Count > 0 && stack[^1].Kind == '(') stack.RemoveAt(stack.Count - 1); }
            else switch (doc.Kw(i))
            {
                case "CASE": stack.Add(('C', false)); break;
                case "WHEN": if (stack.Count > 0 && stack[^1].Kind == 'C') stack[^1] = ('C', true); break;
                case "THEN": if (stack.Count > 0 && stack[^1].Kind == 'C') stack[^1] = ('C', false); break;
                case "END": if (stack.Count > 0 && stack[^1].Kind == 'C') stack.RemoveAt(stack.Count - 1); break;
            }
        }
        var sb = new System.Text.StringBuilder();
        for (var i = stack.Count - 1; i >= 0; i--)
            sb.Append(stack[i].Kind == '(' ? ")" : stack[i].WhenOpen ? " THEN 1 END" : " END");
        return sb.ToString();
    }

    static int CutPoint(DocModel doc, Segment seg, CursorInfo cur, int errDoc)
    {
        var depth = 0;
        for (var i = seg.First; i <= cur.Prev && i <= seg.Last; i++)
        {
            if (doc.IsP(i, '(')) depth++;
            else if (doc.IsP(i, ')')) depth = Math.Max(0, depth - 1);
        }
        var cursorDepth = depth;
        var best = -1;
        for (var i = Math.Max(cur.Prev + 1, seg.First); i <= seg.Last && doc.Toks[i].Start < errDoc; i++)
        {
            if (doc.Toks[i].Start < cur.PrefixEnd) continue;
            if (doc.IsP(i, '(')) { depth++; continue; }
            if (doc.IsP(i, ')')) { depth = Math.Max(0, depth - 1); continue; }
            if (depth > cursorDepth || doc.Kw(i) is not { } kw || !CutKw.Contains(kw)) continue;
            if (kw is "LEFT" or "RIGHT" && doc.IsP(i + 1, '(')) continue;
            if (kw is "JOIN" or "OUTER" && doc.Kw(i - 1) is "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "OUTER") continue;
            best = i;
        }
        return best;
    }

    sealed class ContainVisitor(int pos, int len) : TSqlFragmentVisitor
    {
        public readonly List<TSqlFragment> Found = [];
        public override void Visit(TSqlFragment node)
        {
            if (node.StartOffset >= 0 && node.StartOffset <= pos && pos + len <= node.StartOffset + node.FragmentLength) Found.Add(node);
        }
    }

    /// <summary>Fragments containing the placeholder, outermost first.</summary>
    static List<TSqlFragment> Chain(TSqlStatement stmt, int pos, int len)
    {
        var v = new ContainVisitor(pos, len);
        stmt.Accept(v);
        return v.Found;
    }
}
