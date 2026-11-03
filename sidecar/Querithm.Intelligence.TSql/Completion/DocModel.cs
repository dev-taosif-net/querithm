using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Querithm.Intelligence.TSql.Completion;

enum SegKind : byte
{
    /// <summary>An ordinary statement (or IF/WHILE condition).</summary>
    Statement,
    /// <summary>CREATE/ALTER PROCEDURE|FUNCTION|TRIGGER|VIEW ... up to the body's AS.</summary>
    Header,
    /// <summary>BEGIN / END / ELSE / BEGIN TRY / END CATCH ... — block punctuation, never parsed.</summary>
    Marker,
}

/// <summary>A statement span over significant token indices [First, Last].</summary>
sealed class Segment
{
    public int First;
    public int Last;
    public SegKind Kind;
    /// <summary>Upper-case first keyword (SELECT, WITH, DECLARE, ...), or null.</summary>
    public string? Kw;
    public int Batch;
    /// <summary>For headers: PROCEDURE / FUNCTION / TRIGGER / VIEW.</summary>
    public string? ModuleKind;
    /// <summary>Set when the segment was closed by ';' (the cursor after it is in a new, empty statement).</summary>
    public bool Terminated;
    /// <summary>SELECT ... INTO #temp at the statement's top level.</summary>
    public bool IntoTemp;
    /// <summary>Header only: true once the body's AS was seen.</summary>
    public bool HeaderClosed;
    public int StartOffset;
    public int EndOffset;
}

sealed class Batch
{
    public int Index;
    /// <summary>Significant token index range [FirstTok, EndTok).</summary>
    public int FirstTok;
    public int EndTok;
    public int StartOffset;
    public int EndOffset;
    public int FirstSeg;
    public int EndSeg;
}

/// <summary>
/// Lexed document with batches (GO) and statement segments. Built once per (docId, text) and cached; statement
/// segmentation is token based so a broken statement never hides the rest of the batch (ScriptDom drops the whole
/// batch on a syntax error, which is the normal state of the text at the cursor).
/// </summary>
sealed class DocModel
{
    public required string Text { get; init; }
    public required List<Tok> Toks { get; init; }
    public required List<(int Start, int End, bool Unterminated)> Opaque { get; init; }
    public required List<Batch> Batches { get; init; }
    public required List<Segment> Segs { get; init; }
    internal required SqlLexer.Result Lexed { get; init; }

    static readonly ConcurrentDictionary<string, DocModel> ByDoc = new();

    public static DocModel Get(string text, string? docId)
    {
        DocModel? hit = null;
        if (docId != null && ByDoc.TryGetValue(docId, out hit) && (ReferenceEquals(hit.Text, text) || hit.Text == text)) return hit;
        // Typing: re-lex only around the edit, starting from this document's previous version.
        var model = hit != null ? Build(text, SqlLexer.LexIncremental(hit.Text, hit.Lexed, text)) : Build(text);
        if (docId != null)
        {
            if (ByDoc.Count > 256) ByDoc.Clear();
            ByDoc[docId] = model;
        }
        return model;
    }

    /// <summary>A closed tab or deleted notebook cell (lang.forget).</summary>
    public static void Forget(string docId) => ByDoc.TryRemove(docId, out _);

    internal static bool IsCached(string docId) => ByDoc.ContainsKey(docId);

    public static DocModel Build(string text) => Build(text, SqlLexer.Lex(text));

    static DocModel Build(string text, SqlLexer.Result lex)
    {
        var model = new DocModel { Text = text, Toks = lex.Tokens, Opaque = lex.Opaque, Batches = [], Segs = [], Lexed = lex };
        model.SplitBatches();
        foreach (var b in model.Batches) new Segmenter(model, b).Run();
        return model;
    }

    /// <summary>Inside a comment or string (strictly after its start; a closed token's end is outside).</summary>
    public bool InOpaque(int offset)
    {
        int lo = 0, hi = Opaque.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var (s, e, unterminated) = Opaque[mid];
            if (offset <= s) hi = mid - 1;
            else if (offset > e || (offset == e && !unterminated && !IsLineComment(s))) lo = mid + 1;
            else return true;
        }
        return false;
    }

    bool IsLineComment(int start) => start + 1 < Text.Length && Text[start] == '-' && Text[start + 1] == '-';

    /// <summary>Index of the last significant token that ends at or before <paramref name="offset"/>, or -1.</summary>
    public int LastTokBefore(int offset)
    {
        int lo = 0, hi = Toks.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Toks[mid].End <= offset) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    public string TokText(int i) => Text.Substring(Toks[i].Start, Toks[i].Length);
    public string? Kw(int i) => i >= 0 && i < Toks.Count ? Toks[i].Kw : null;
    public bool IsP(int i, char c) => i >= 0 && i < Toks.Count && Toks[i].IsPunct(Text, c);

    public Batch BatchAt(int offset)
    {
        foreach (var b in Batches) if (offset <= b.EndOffset || b == Batches[^1]) return b;
        return Batches[^1];
    }

    void SplitBatches()
    {
        var start = 0;
        var startOffset = 0;
        for (var i = 0; i < Toks.Count; i++)
        {
            var t = Toks[i];
            if (t.Kw != "GO" || !AloneOnLine(t)) continue;
            AddBatch(start, i, startOffset, t.Start);
            var next = i + 1;
            // GO n
            if (next < Toks.Count && Toks[next].Kind == TK.Number && !Text.AsSpan(t.End, Toks[next].Start - t.End).Contains('\n')) next++;
            start = next;
            startOffset = next < Toks.Count ? (next > i + 1 ? Toks[next - 1].End : t.End) : t.End;
            i = next - 1;
        }
        AddBatch(start, Toks.Count, startOffset, Text.Length);
    }

    void AddBatch(int first, int end, int so, int eo) =>
        Batches.Add(new Batch { Index = Batches.Count, FirstTok = first, EndTok = end, StartOffset = so, EndOffset = eo });

    bool AloneOnLine(Tok t)
    {
        for (var j = t.Start - 1; j >= 0 && Text[j] != '\n'; j--) if (!char.IsWhiteSpace(Text[j])) return false;
        return true;
    }

    /// <summary>Forward statement splitter for one batch (rules in docs/ARCHITECTURE.md §9 tier 1, extended).</summary>
    sealed class Segmenter(DocModel m, Batch b)
    {
        static readonly FrozenSet<string> StatementKw = new[]
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "WITH", "DECLARE", "SET", "EXEC", "EXECUTE", "IF", "ELSE", "WHILE",
            "BEGIN", "END", "RETURN", "PRINT", "CREATE", "ALTER", "DROP", "TRUNCATE", "OPEN", "FETCH", "CLOSE", "DEALLOCATE",
            "THROW", "RAISERROR", "USE", "BREAK", "CONTINUE", "GOTO", "WAITFOR", "GRANT", "REVOKE", "DENY", "COMMIT", "ROLLBACK",
            "SAVE", "DBCC", "BACKUP", "RESTORE", "CHECKPOINT", "KILL", "BULK", "REVERT", "ENABLE", "DISABLE",
        }.ToFrozenSet();

        Segment? cur;
        int depth;
        int caseDepth;
        // Per-statement state for continuation rules.
        bool seenSet, seenSource, seenMain, isCursorDecl;
        string? mainKw;

        List<Tok> T => m.Toks;

        public void Run()
        {
            b.FirstSeg = m.Segs.Count;
            for (var i = b.FirstTok; i < b.EndTok; i++)
            {
                var t = T[i];
                if (t.IsPunct(m.Text, ';'))
                {
                    if (cur != null) { cur.Terminated = true; Close(i - 1); }
                    depth = 0; caseDepth = 0;
                    continue;
                }
                if (cur == null) { i = Start(i); continue; }
                if (t.Kind == TK.Punct)
                {
                    if (t.IsPunct(m.Text, '(')) depth++;
                    else if (t.IsPunct(m.Text, ')')) depth = Math.Max(0, depth - 1);
                    continue;
                }
                var kw = t.Kw;
                if (kw == null) continue;
                if (kw == "CASE") { caseDepth++; continue; }
                if (kw is "VALUES" or "DEFAULT" && depth == 0) seenSource = true;
                if (kw == "INTO" && depth == 0 && i + 1 < b.EndTok && T[i + 1].IsName() && m.Text[T[i + 1].Start + (T[i + 1].Kind == TK.Quoted ? 1 : 0)] == '#') cur.IntoTemp = true;
                if (kw == "END" && caseDepth > 0) { caseDepth--; continue; }
                if (cur.Kind == SegKind.Header && !cur.HeaderClosed)
                {
                    if (kw == "AS" && depth == 0 && i + 1 < b.EndTok && (T[i + 1].Kw is { } nk && (StatementKw.Contains(nk) || nk == "EXTERNAL")))
                    {
                        cur.HeaderClosed = true;
                        Close(i);
                    }
                    continue;
                }
                if (!StatementKw.Contains(kw)) continue;
                if (depth > 0)
                {
                    if (!StrongInParens(i, kw)) continue;
                    depth = 0; caseDepth = 0;
                    Close(i - 1);
                    i = Start(i);
                    continue;
                }
                if (Continues(i, kw)) continue;
                Close(i - 1);
                i = Start(i);
            }
            if (cur != null) Close(b.EndTok - 1);
            b.EndSeg = m.Segs.Count;
            b.StartOffset = b.FirstTok < b.EndTok ? Math.Min(b.StartOffset, T[b.FirstTok].Start) : b.StartOffset;
        }

        string? Prev(int i) => i - 1 >= b.FirstTok ? T[i - 1].Kw : null;
        bool PrevP(int i, char c) => i - 1 >= b.FirstTok && T[i - 1].IsPunct(m.Text, c);

        bool StrongInParens(int i, string kw) => kw switch
        {
            "DECLARE" or "CREATE" or "ALTER" or "MERGE" or "BEGIN" or "PRINT" or "RAISERROR" or "THROW" or "IF" or "WHILE" or "RETURN" => true,
            "EXEC" or "EXECUTE" => Prev(i) != "WITH" && cur?.Kw is not ("GRANT" or "REVOKE" or "DENY"),
            "INSERT" or "DELETE" => Prev(i) is not ("ON" or "FOR" or "AFTER" or "OF") && !PrevP(i, ','),
            "UPDATE" => Prev(i) is not ("ON" or "FOR" or "AFTER" or "OF") && !PrevP(i, ',') && !(i + 1 < b.EndTok && T[i + 1].IsPunct(m.Text, '(')),
            "END" => caseDepth == 0,
            _ => false,
        };

        bool Continues(int i, string kw)
        {
            var prev = Prev(i);
            var ck = cur!.Kw;
            if (ck is "GRANT" or "REVOKE" or "DENY") return kw is not ("SELECT" or "INSERT" or "DECLARE" or "IF" or "BEGIN" or "END" or "CREATE" or "ALTER" or "PRINT") || prev is "GRANT" or "DENY" or "REVOKE" || PrevP(i, ',');
            switch (kw)
            {
                case "SELECT":
                    if (prev is "UNION" or "ALL" or "EXCEPT" or "INTERSECT") return true;
                    if (ck == "WITH" && !seenMain) { seenMain = true; mainKw = kw; return true; }
                    if (ck == "INSERT" && !seenSource) { seenSource = true; return true; }
                    if (ck == "DECLARE" && isCursorDecl) return true;
                    if (prev == "FOR") return true;
                    return false;
                case "WITH":
                    if (!IsCteStart(i)) return true;
                    return false;
                case "INSERT" or "DELETE" or "MERGE":
                    if (prev is "THEN" or "ON" or "FOR" or "AFTER" or "OF") return true;
                    if (ck == "WITH" && !seenMain) { seenMain = true; mainKw = kw; return true; }
                    return false;
                case "UPDATE":
                    if (prev is "THEN" or "ON" or "FOR" or "OF") return true;
                    if (i + 1 < b.EndTok && T[i + 1].IsPunct(m.Text, '(')) return true; // UPDATE(col) in a trigger
                    if (ck == "WITH" && !seenMain) { seenMain = true; mainKw = kw; return true; }
                    return false;
                case "SET":
                    if ((ck == "UPDATE" || mainKw == "UPDATE") && !seenSet) { seenSet = true; return true; }
                    if (prev is "UPDATE" or "DELETE") return true; // MERGE ... THEN UPDATE SET / ON DELETE SET NULL
                    if (ck == "ALTER") return true;
                    return false;
                case "EXEC" or "EXECUTE":
                    if (ck == "INSERT" && !seenSource) { seenSource = true; return true; }
                    if (prev == "WITH" || PrevP(i, ',')) return true; // WITH EXECUTE AS
                    return false;
                case "IF":
                    return ck == "DROP" || ck == "CREATE"; // DROP TABLE IF EXISTS
                case "FETCH":
                    return prev is "ROWS" or "ROW";
                case "ALTER":
                    return prev == "OR";
                case "DROP" or "ENABLE" or "DISABLE":
                    return ck == "ALTER";
                case "OPEN" or "CLOSE":
                    return ck == "ALTER";
                case "BEGIN":
                    return false;
                case "END":
                    return false;
                case "RETURN":
                    return false;
                default:
                    return false;
            }
        }

        bool IsCteStart(int i)
        {
            // WITH name [(cols)] AS (
            var j = i + 1;
            if (j >= b.EndTok || !T[j].IsName() || (T[j].Kw != null && T[j].Kw is "NOLOCK" or "TIES" or "ROLLUP" or "CUBE")) return false;
            j++;
            if (j < b.EndTok && T[j].IsPunct(m.Text, '('))
            {
                var d = 0;
                for (; j < b.EndTok; j++)
                {
                    if (T[j].IsPunct(m.Text, '(')) d++;
                    else if (T[j].IsPunct(m.Text, ')') && --d == 0) { j++; break; }
                }
            }
            return j + 1 < b.EndTok && T[j].Kw == "AS" && T[j + 1].IsPunct(m.Text, '(');
        }

        /// <summary>Opens a segment at token i; returns the last token index it consumed.</summary>
        int Start(int i)
        {
            var t = T[i];
            seenSet = seenSource = seenMain = isCursorDecl = false;
            mainKw = null;
            caseDepth = 0;
            depth = 0;
            cur = new Segment { First = i, Last = i, Kind = SegKind.Statement, Kw = t.Kw, Batch = b.Index, StartOffset = t.Start };
            var kw = t.Kw;
            if (t.IsPunct(m.Text, '(')) depth = 1;
            if (kw == "CASE") caseDepth = 1;
            if (kw is "CREATE" or "ALTER")
            {
                var j = i + 1;
                if (j < b.EndTok && T[j].Kw == "OR") j += 2;
                var mk = j < b.EndTok ? T[j].Kw : null;
                mk = mk switch { "PROC" or "PROCEDURE" => "PROCEDURE", "FUNCTION" => "FUNCTION", "TRIGGER" => "TRIGGER", "VIEW" => "VIEW", _ => null };
                if (mk != null) { cur.Kind = SegKind.Header; cur.ModuleKind = mk; }
            }
            else if (kw is "BEGIN" or "END" or "ELSE")
            {
                var j = i + 1;
                var nk = j < b.EndTok ? T[j].Kw : null;
                if (kw == "BEGIN" && nk is "TRAN" or "TRANSACTION" or "DISTRIBUTED" or "DIALOG" or "CONVERSATION") return i;
                if (kw == "END" && nk == "CONVERSATION") return i;
                cur.Kind = SegKind.Marker;
                if (nk is "TRY" or "CATCH") { Close(j); return j; }
                Close(i);
            }
            else if (kw == "DECLARE")
            {
                // DECLARE name CURSOR ...
                if (i + 2 < b.EndTok && T[i + 1].Kind == TK.Word && T[i + 2].Kw is "CURSOR" or "INSENSITIVE" or "SCROLL") isCursorDecl = true;
            }
            return i;
        }

        void Close(int last)
        {
            if (cur == null) return;
            cur.Last = Math.Max(cur.First, Math.Min(last, b.EndTok - 1));
            cur.EndOffset = T[cur.Last].End;
            m.Segs.Add(cur);
            cur = null;
        }
    }
}
