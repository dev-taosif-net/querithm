namespace Querithm.Intelligence.PgSql.Completion;

/// <summary>
/// Where the cursor is: the outer script or a dollar-quoted body (nested bodies too), the statement around it, and the
/// word being typed. Null when the cursor is inside a string, comment or a dollar-quoted literal that is not code.
/// </summary>
sealed class PgCompletionContext
{
    public required PgDocument Doc { get; init; }
    /// <summary>The code tokens of the region the cursor is in.</summary>
    public required List<Tok> Code { get; init; }
    public bool InBody { get; init; }
    /// <summary>In a body: the tokens of the CREATE FUNCTION / PROCEDURE / DO statement that holds it.</summary>
    public List<Tok> Header { get; init; } = [];
    /// <summary>The statement around the cursor (all of it: a FROM after the cursor counts).</summary>
    public required List<Tok> Statement { get; init; }
    /// <summary>The statement's tokens before the word being typed.</summary>
    public required List<Tok> Before { get; init; }
    public required int WordStart { get; init; }
    public string Prefix { get; init; } = "";

    static readonly HashSet<string> BodyBoundary = new(["BEGIN", "DECLARE", "LOOP", "THEN", "ELSE", "ELSIF", "ELSEIF", "EXCEPTION"], StringComparer.OrdinalIgnoreCase);

    public static PgCompletionContext? At(PgDocument doc, int offset)
    {
        var raw = doc.Raw;
        List<PgToken>? outerRaw = null;
        PgToken? dollar = null;
        while (true)
        {
            var i = Containing(raw, offset);
            if (i < 0) break;
            var t = raw[i];
            var inside = offset > t.Offset && (offset < t.Offset + t.Length || (offset == t.Offset + t.Length && Open(t)));
            if (!inside) break;
            if (t.Kind == PgTokenKind.DollarString)
            {
                if (offset <= t.Offset + PgDocument.TagLength(t) || !PgDocument.IsBody(raw, i)) return null;
                outerRaw = raw;
                dollar = t;
                raw = doc.Body(t);
                continue;
            }
            if (t.Kind is PgTokenKind.String or PgTokenKind.Comment) return null;
            break;
        }
        var code = doc.CodeOf(raw);
        var word = code.FindLastIndex(t => t.Offset < offset && offset <= t.End && (t.Kind == PgTokenKind.Word || t.Kind == PgTokenKind.QuotedIdent));
        var wordStart = word >= 0 ? code[word].Offset : offset;
        var prefix = word >= 0 ? doc.Text[code[word].Offset..offset].Trim('"') : "";
        var statement = dollar == null ? OuterStatement(doc, wordStart) : BodyStatement(code, wordStart);
        var header = dollar is { } d && outerRaw != null
            ? (ReferenceEquals(outerRaw, doc.Raw) ? OuterStatement(doc, d.Offset) : BodyStatement(doc.CodeOf(outerRaw), d.Offset)).Where(x => x.Offset != d.Offset).ToList()
            : [];
        return new PgCompletionContext
        {
            Doc = doc, Code = code, InBody = dollar != null, Header = header, Statement = statement,
            Before = statement.Where(x => x.End <= wordStart && x.Offset < wordStart).ToList(), WordStart = wordStart, Prefix = prefix,
        };
    }

    /// <summary>An unterminated string, quoted name or block comment, or a line comment, still holds the end of the text.</summary>
    static bool Open(PgToken t) => t.Kind switch
    {
        PgTokenKind.Comment => t.Text.StartsWith("--", StringComparison.Ordinal) || !t.Text.EndsWith("*/", StringComparison.Ordinal),
        PgTokenKind.String => t.Text.Length < 2 || !t.Text.EndsWith('\'') || t.Text.Length == 2 && t.Text[0] != '\'',
        PgTokenKind.DollarString => !PgDocument.Terminated(t),
        _ => false,
    };

    /// <summary>The last token starting before <paramref name="offset"/>.</summary>
    static int Containing(List<PgToken> raw, int offset)
    {
        int lo = 0, hi = raw.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (raw[mid].Offset < offset) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    static List<Tok> OuterStatement(PgDocument doc, int offset)
    {
        List<PgToken>? pick = null;
        foreach (var s in doc.Statements)
        {
            var first = s.FirstOrDefault(t => !t.IsTrivia);
            if (first.Text == null) continue;
            if (pick == null || first.Offset < offset) pick = s;
            else break;
        }
        var code = PgDocument.Code(pick ?? []);
        // A ';' right before the cursor ends that statement: a new one starts here.
        return code.Count > 0 && code[^1].IsP(";") && code[^1].End <= offset ? [] : code.Where(t => !t.IsP(";")).ToList();
    }

    /// <summary>
    /// The statement around <paramref name="offset"/> inside a PL/pgSQL body: between ';' and block words (BEGIN, LOOP,
    /// THEN, …) at the top level; a CASE … END expression keeps its THEN / ELSE.
    /// </summary>
    public static List<Tok> BodyStatement(List<Tok> code, int offset)
    {
        int start = 0, end = code.Count, depth = 0, caseDepth = 0;
        for (var i = 0; i < code.Count; i++)
        {
            var t = code[i];
            if (t.IsP("(")) { depth++; continue; }
            if (t.IsP(")")) { depth = Math.Max(0, depth - 1); continue; }
            if (depth > 0) continue;
            if (t.Is("CASE")) caseDepth++;
            else if (t.Is("END") && caseDepth > 0 && !(i + 1 < code.Count && (code[i + 1].Is("IF") || code[i + 1].Is("LOOP")))) caseDepth--;
            var boundary = t.IsP(";") || (caseDepth == 0 && t.Kind == PgTokenKind.Word && BodyBoundary.Contains(t.Text)) || t.IsP(">>");
            if (t.IsP(";")) caseDepth = 0;
            if (!boundary) continue;
            if (t.End <= offset) start = i + 1;
            else { end = i; break; }
        }
        return code.GetRange(start, Math.Max(0, end - start));
    }
}
