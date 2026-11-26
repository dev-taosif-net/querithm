using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Querithm.Contract;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.PgSql.Completion;

namespace Querithm.Intelligence.Oracle;

/// <summary>
/// Tier-2 completion and signature help for Oracle (D4 S09): PostgreSQL's token-scoped engine on <see cref="OracleTokenizer"/>
/// tokens and <see cref="OracleSplitter"/> statements, under Oracle's rules — unquoted names fold to upper case and compare
/// exactly (D0e), <c>"x"</c> is exact and never needs an escape, unqualified names resolve in the current schema (the last
/// <c>ALTER SESSION SET CURRENT_SCHEMA</c> before the cursor, else the signed-in user's) and then through PUBLIC synonyms, a
/// synonym reads its target's columns, and calls take <c>name =&gt; value</c> arguments. Nothing is offered inside a string
/// (<c>q'[…]'</c> included), a comment, a hint, a database link or a SQL*Plus command line.
/// <para>
/// PL/SQL units are split into the statements of their body — between <c>;</c> and the block words (DECLARE, BEGIN, THEN,
/// ELSE, ELSIF, LOOP, EXCEPTION, a label, and the IS / AS of a unit, subprogram or cursor header) — so the SQL inside a block,
/// a cursor's query included, completes like SQL. Declared variables and parameters are not offered (recorded in D4 S09).
/// </para>
/// Signature help knows standalone procedures and functions (ALL_ARGUMENTS, through the snapshot) and package subprograms
/// from the specification's member lines (D4 S08: display text, one line per overload).
/// </summary>
public static class OracleCompletion
{
    /// <summary>Where unqualified names resolve after the current schema.</summary>
    public const string Public = "PUBLIC";

    /// <summary>Package members are labelled schema.package.member: their <see cref="SnapObject.Schema"/> holds "schema.package" as written.</summary>
    const long MemberId = long.MinValue;

    static PgToken ToPg(OracleToken t) => new(t.Kind switch
    {
        OracleTokenKind.Word => PgTokenKind.Word, OracleTokenKind.QuotedIdent => PgTokenKind.QuotedIdent, OracleTokenKind.String => PgTokenKind.String,
        OracleTokenKind.Number => PgTokenKind.Number, OracleTokenKind.Operator => PgTokenKind.Operator, OracleTokenKind.Punct => PgTokenKind.Punct,
        OracleTokenKind.Bind => PgTokenKind.Param,
        // A hint and a database link (@name after a name) are not code completion can help with.
        OracleTokenKind.Comment or OracleTokenKind.Hint or OracleTokenKind.DbLink => PgTokenKind.Comment,
        _ => PgTokenKind.Whitespace,
    }, t.Offset, t.Length, t.Line, t.Text);

    /// <summary>
    /// The tokens and split of the last text: the schema switches, Tokenize and Statements read the same document one after
    /// the other. Code audit 2026-11-25 (M13): the switches split the text on their own and Tokenize lexed it again, so a
    /// cold request lexed the document three times; now once. Cleared when the outermost <see cref="Use"/> scope ends, so
    /// a pool thread does not hold the last document (up to 16 MB) after its request.
    /// </summary>
    [ThreadStatic] static (string Text, List<OracleToken> Tokens, List<OracleStatement> Split)? _last;

    static (List<OracleToken> Tokens, List<OracleStatement> Split) Lexed(string text)
    {
        if (_last is { } l && ReferenceEquals(l.Text, text)) return (l.Tokens, l.Split);
        if (text.Length >= LargeText && _shared is { } s && string.Equals(s.Text, text, StringComparison.Ordinal))
        {
            _last = (text, s.Tokens, s.Split); // the next calls on this thread compare by reference, not 16 MB again (M16)
            return (s.Tokens, s.Split);
        }
        var tokens = OracleTokenizer.Tokenize(text);
        var split = OracleSplitter.Split(text, tokens);
        if (text.Length >= LargeText)
        {
            Interlocked.Increment(ref _largeLexes);
            _shared = new Lex(text, tokens, split);
        }
        _last = (text, tokens, split);
        return (tokens, split);
    }

    /// <summary>The length from which completion reads a document in the background (PgSql's BackgroundBuild.MinChars).</summary>
    const int LargeText = 2_000_000;

    sealed record Lex(string Text, List<OracleToken> Tokens, List<OracleStatement> Split);

    /// <summary>
    /// The last long text lexed, until completion's document build takes it (code audit 2026-11-25, second round): the
    /// schema-switch scan and the document are read in the background on two pool threads, and each lexed a 16 MB text
    /// of its own. Compared by value (each request brings its own copy of the text); one document at most, process-wide.
    /// </summary>
    static volatile Lex? _shared;

    static int _largeLexes;

    /// <summary>Lets go of the shared lex (lang.forget: the document may be the one it holds).</summary>
    internal static void ClearShared() => _shared = null;

    /// <summary>Long texts lexed so far (tests).</summary>
    internal static int LargeLexes => Volatile.Read(ref _largeLexes);

    static List<OracleStatement> Split(string text) => Lexed(text).Split;

    /// <summary>Whether this thread still holds a document (tests).</summary>
    internal static bool HoldsDocument => _last != null;

    /// <summary>The document's tokens; a SQL*Plus command line reads as a comment (nothing to complete there).</summary>
    static List<PgToken> Tokenize(string text)
    {
        var tokens = Tier2Cancellation.Map(Lexed(text).Tokens, ToPg);
        foreach (var st in Split(text))
        {
            if (st.Kind != OracleStatementKind.ClientCommand || st.Tokens.Count == 0) continue;
            var from = st.Tokens[0].Offset;
            var to = st.Tokens[^1].End;
            var i = First(tokens, from);
            for (; i < tokens.Count && tokens[i].Offset < to; i++) tokens[i] = tokens[i] with { Kind = PgTokenKind.Comment };
        }
        return tokens;
    }

    /// <summary>The index of the first token at or after <paramref name="offset"/>.</summary>
    static int First(List<PgToken> tokens, int offset)
    {
        int lo = 0, hi = tokens.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (tokens[mid].Offset < offset) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    static readonly HashSet<string> BlockWords = new(["BEGIN", "DECLARE", "THEN", "ELSE", "ELSIF", "LOOP", "EXCEPTION"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The first words of a header whose IS / AS opens a body or a query (CREATE …, a nested subprogram, a cursor, a type).</summary>
    static readonly HashSet<string> HeaderWords = new(["CREATE", "PROCEDURE", "FUNCTION", "PACKAGE", "TRIGGER", "TYPE", "SUBTYPE", "CURSOR"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The statements completion sees, each ending in a ';' token where a terminator ended it (a new statement starts after
    /// it). Plain SQL is one statement; a PL/SQL unit is cut into the statements of its body (see the class remarks).
    /// </summary>
    static List<List<PgToken>> Statements(string text, List<PgToken> tokens)
    {
        // The document build reads the tokens (Tokenize), then the split (here): after this nothing needs the shared lex.
        if (text.Length >= LargeText)
        {
            var split = Split(text);
            // Only the entry of this text: another thread's newer one stays (fourth round, M16).
            if (_shared is { } mine && ReferenceEquals(mine.Split, split)) Interlocked.CompareExchange(ref _shared, null, mine);
            return StatementsOf(split, tokens);
        }
        return StatementsOf(Split(text), tokens);
    }

    static List<List<PgToken>> StatementsOf(List<OracleStatement> split, List<PgToken> tokens)
    {
        var result = new List<List<PgToken>>();
        foreach (var st in split)
        {
            if (st.Kind == OracleStatementKind.ClientCommand) continue;
            var own = Tier2Cancellation.Map(st.Tokens, ToPg);
            if (st.Kind == OracleStatementKind.PlSql)
            {
                BodyStatements(own, result);
                continue;
            }
            // The ';' or '/' that ended it is outside the statement's tokens.
            var next = First(tokens, st.End);
            while (next < tokens.Count && tokens[next].IsTrivia) next++;
            if (next < tokens.Count && tokens[next].Kind is PgTokenKind.Punct or PgTokenKind.Operator && tokens[next].Text is ";" or "/")
                own.Add(new PgToken(PgTokenKind.Punct, tokens[next].Offset, 1, tokens[next].Line, ";"));
            result.Add(own);
        }
        return result;
    }

    static void BodyStatements(List<PgToken> unit, List<List<PgToken>> result)
    {
        var current = new List<PgToken>();
        int depth = 0, caseDepth = 0;
        PgToken? firstCode = null;
        void Flush(PgToken? end)
        {
            if (end is { } e) current.Add(e);
            if (current.Count > 0) result.Add(current);
            current = [];
            firstCode = null;
        }
        for (var i = 0; i < unit.Count; i++)
        {
            var t = unit[i];
            if (t.IsTrivia) { current.Add(t); continue; }
            var punct = t.Kind is PgTokenKind.Punct or PgTokenKind.Operator;
            if (punct && t.Text == "(") depth++;
            else if (punct && t.Text == ")") depth = Math.Max(0, depth - 1);
            if (depth == 0 && t.Kind == PgTokenKind.Word)
            {
                if (t.Is("CASE")) caseDepth++;
                else if (t.Is("END") && caseDepth > 0 && !NextIs(unit, i, "IF", "LOOP")) caseDepth--;
            }
            var block = depth == 0 && caseDepth == 0 && t.Kind == PgTokenKind.Word &&
                        (BlockWords.Contains(t.Text) || (t.Is("IS") || t.Is("AS")) && firstCode is { } f && HeaderWords.Contains(f.Text));
            if (depth == 0 && punct && t.Text == ">>") block = true;
            if (depth == 0 && punct && t.Text == ";")
            {
                caseDepth = 0;
                current.Add(t);
                Flush(null);
                continue;
            }
            if (block)
            {
                // The block word stands alone: right after it a statement starts, before it the previous one ended.
                Flush(new PgToken(PgTokenKind.Punct, t.Offset, 0, t.Line, ";"));
                result.Add([t, new PgToken(PgTokenKind.Punct, t.Offset + t.Length, 0, t.Line, ";")]);
                continue;
            }
            firstCode ??= t;
            current.Add(t);
        }
        Flush(null);
    }

    static bool NextIs(List<PgToken> tokens, int i, params string[] words)
    {
        for (var k = i + 1; k < tokens.Count; k++)
        {
            if (tokens[k].IsTrivia) continue;
            return words.Any(w => tokens[k].Is(w));
        }
        return false;
    }

    /// <summary>A quoted name, also while it is still being typed (no closing quote yet). Oracle has no escape inside one.</summary>
    static string Unquote(string t) => t.Length == 0 ? t : t.Length >= 2 && t[^1] == '"' ? t[1..^1] : t[1..];

    /// <summary>'…' with '' doubled, N'…', and q'X…X' / nq'X…X' (X any character; [ { ( &lt; close with their partner).</summary>
    internal static bool Unterminated(PgToken token)
    {
        var t = token.Text;
        var i = 0;
        if (i < t.Length && t[i] is 'n' or 'N') i++;
        if (i < t.Length && t[i] is 'q' or 'Q')
        {
            if (t.Length < i + 3) return true;
            var close = t[i + 2] switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', var c => c };
            return !(t.Length >= i + 5 && t[^1] == '\'' && t[^2] == close);
        }
        for (i++; i < t.Length; i++)
        {
            if (t[i] != '\'') continue;
            if (i + 1 < t.Length && t[i + 1] == '\'') { i++; continue; }
            return i != t.Length - 1;
        }
        return true;
    }

    /// <summary>MINUS joins two queries as UNION does, each operand a query level of its own.</summary>
    static readonly IReadOnlySet<string> SetOperators = new HashSet<string>([.. PgCompletionRules.StandardSetOperators, "MINUS"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Keywords that start a clause after a FROM item only before these tokens; elsewhere they are names (FROM t sample).</summary>
    static readonly Dictionary<string, string[]> ClauseBefore = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PARTITION"] = ["(", "FOR"], ["SUBPARTITION"] = ["(", "FOR"], ["SAMPLE"] = ["(", "BLOCK"], ["PIVOT"] = ["(", "XML"],
        ["UNPIVOT"] = ["(", "INCLUDE", "EXCLUDE"], ["VERSIONS"] = ["BETWEEN", "PERIOD"], ["MATCH_RECOGNIZE"] = ["("],
    };

    /// <summary>
    /// A word after a FROM item that starts a clause instead of naming the alias (audit H10): a SQL reserved word, which no
    /// bare alias can be (START WITH, CONNECT BY, MINUS, AS OF), or a clause keyword before what it takes (PARTITION (p),
    /// SAMPLE (n), PIVOT (…), VERSIONS BETWEEN, MATCH_RECOGNIZE (…)).
    /// </summary>
    static bool EndsFromItem(string word, string? next) =>
        OracleKeywords.IsSqlReserved(word)
        || (next != null && ClauseBefore.TryGetValue(word, out var before) && before.Contains(next, StringComparer.OrdinalIgnoreCase));

    /// <summary>One instance per current schema, bounded (audit M13): the login schema is untrusted and a typed one grows a letter at a time.</summary>
    static readonly RulesCache<string> Cache = new(Create, StringComparer.Ordinal);

    internal static int CachedRules => Cache.Count;

    /// <summary>Rules with <paramref name="schema"/> as the current schema (one instance each: it caches its snapshot index).</summary>
    public static PgCompletionRules RulesFor(string schema) => Cache.Get(schema);

    static PgCompletionRules Create(string s) => new()
    {
        Id = $"oracle:{s}", DocumentKey = "oracle", ReleaseThread = static () => _last = null, Names = StringComparer.Ordinal, FoldsUnquoted = true, Fold = w => w.ToUpperInvariant(), DefaultSchema = s,
        SearchPath = [Public], IsReserved = OracleKeywords.IsReserved, IsPlain = OracleIdentifiers.CanBeBare, DollarBodiesAreCode = false,
        Tokenize = Tokenize, Statements = Statements, Unquote = Unquote, Delimit = d => "\"" + d + "\"", NamedArguments = true,
        StringOpen = Unterminated, MemberRoutines = Members, RoutineLabel = Label, EndsFromItem = EndsFromItem, SetOperators = SetOperators,
        // The types CAST(x AS …) takes.
        BuiltinTypes =
        [
            "NUMBER", "INTEGER", "FLOAT", "BINARY_FLOAT", "BINARY_DOUBLE", "VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR", "CLOB", "NCLOB", "BLOB",
            "RAW", "DATE", "TIMESTAMP", "TIMESTAMP WITH TIME ZONE", "TIMESTAMP WITH LOCAL TIME ZONE", "INTERVAL YEAR TO MONTH",
            "INTERVAL DAY TO SECOND", "ROWID", "UROWID", "JSON", "XMLTYPE", "BOOLEAN", "VECTOR",
        ],
    };

    /// <summary>
    /// The schema completion resolves unqualified names in at <paramref name="offset"/>: the last <c>ALTER SESSION SET
    /// CURRENT_SCHEMA</c> before it (found once per text, <see cref="DocumentScan{T}"/>), else <paramref name="login"/>.
    /// </summary>
    public static string SchemaAt(string? docId, string text, int offset, string? login)
    {
        string? schema = null;
        foreach (var (start, s) in DocumentScan<List<(int Start, string Schema)>>.Get($"oracle|{docId}", text, Switches, static () => _last = null))
        {
            if (start >= offset) break;
            schema = s;
        }
        return schema ?? login ?? "";
    }

    static List<(int Start, string Schema)> Switches(string text)
    {
        var list = new List<(int, string)>();
        foreach (var st in Split(text)) // lexed once with the tokens completion reads next (M13)
        {
            if (st.Kind != OracleStatementKind.Sql) continue;
            var c = st.Code.Take(6).ToList();
            if (c.Count >= 6 && c[0].Is("ALTER") && c[1].Is("SESSION") && c[2].Is("SET") && c[3].Is("CURRENT_SCHEMA") && c[4].IsP("=")
                && c[5].Kind is OracleTokenKind.Word or OracleTokenKind.QuotedIdent)
                list.Add((st.Start, OracleIdentifiers.Identity(c[5])));
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------ package members

    static string Label(SnapObject o) => o.Id == MemberId ? $"{o.Schema}.{OracleIdentifiers.QuoteIfNeeded(o.Name)}" : $"{Q(o.Schema)}.{Q(o.Name)}";

    static string Q(string name) => OracleIdentifiers.Invalid(name) == null ? OracleIdentifiers.QuoteIfNeeded(name) : name;

    sealed class PackageIndex
    {
        public required Dictionary<long, SnapObject> ById { get; init; }
        public required Dictionary<string, List<SnapObject>> Packages { get; init; }
        public readonly ConcurrentDictionary<long, List<SnapObject>> Parsed = new();
    }

    static readonly ConditionalWeakTable<SchemaSnapshot, PackageIndex> Indexes = new();

    static PackageIndex IndexOf(SchemaSnapshot snap) => Indexes.GetValue(snap, s =>
    {
        var byId = new Dictionary<long, SnapObject>();
        var packages = new Dictionary<string, List<SnapObject>>(StringComparer.Ordinal);
        foreach (var o in s.Objects)
        {
            byId.TryAdd(o.Id, o);
            if (o.Kind is ObjectKind.Package or ObjectKind.Synonym)
            {
                if (!packages.TryGetValue(o.Name, out var list)) packages[o.Name] = list = [];
                list.Add(o);
            }
        }
        return new PackageIndex { ById = byId, Packages = packages };
    });

    /// <summary>
    /// pkg.member / schema.pkg.member: the package in the current schema, then a PUBLIC synonym of one (or the named schema),
    /// its member lines for <paramref name="chain"/>'s last name, one routine per overload.
    /// </summary>
    static IReadOnlyList<SnapObject> Members(SchemaSnapshot snap, IReadOnlyList<string> chain)
    {
        var index = IndexOf(snap);
        var member = chain[^1];
        var package = chain[^2];
        string? schema = chain.Count >= 3 ? chain[^3] : null;
        if (!index.Packages.TryGetValue(package, out var candidates)) return [];
        var current = _schema ?? "";
        SnapObject? Resolve(SnapObject o)
        {
            for (var hops = 0; hops < 8 && o.Kind == ObjectKind.Synonym; hops++)
            {
                if (o.TargetObjectId is not { } t || !index.ById.TryGetValue(t, out var next)) return null;
                o = next;
            }
            return o.Kind == ObjectKind.Package ? o : null;
        }
        var pick = schema != null
            ? candidates.FirstOrDefault(o => o.Schema == schema && o.Kind == ObjectKind.Package)
            : candidates.Where(o => o.Schema == current).Select(Resolve).FirstOrDefault(o => o != null)
              ?? candidates.Where(o => o.Schema == Public).Select(Resolve).FirstOrDefault(o => o != null);
        if (pick == null) return [];
        var all = index.Parsed.GetOrAdd(pick.Id, _ => (pick.Members ?? []).Select(line => ParseMember(pick, line)).OfType<SnapObject>().ToList());
        return all.Where(o => o.Name == member).ToList();
    }

    /// <summary>The current schema of the call running on this thread (set by <see cref="Use"/>).</summary>
    [ThreadStatic] static string? _schema;

    /// <summary>Scopes Oracle's rules for <paramref name="schema"/> to this thread's call (the engine's rules and the member lookup's schema).</summary>
    public static IDisposable Use(string schema)
    {
        var previous = _schema;
        _schema = schema;
        var rules = PgCompletionRules.Use(RulesFor(schema));
        return new Restore(previous, rules);
    }

    sealed class Restore(string? previous, IDisposable rules) : IDisposable
    {
        public void Dispose()
        {
            rules.Dispose();
            _schema = previous;
            if (previous == null) _last = null; // the request is over: do not hold its document on this pool thread (M13)
        }
    }

    /// <summary>
    /// One member line as S08 writes it — <c>P(A IN NUMBER, B IN OUT VARCHAR2)</c>, <c>F(X IN NUMBER) RETURN NUMBER</c>,
    /// <c>Q</c> — as a routine of the package; null when the line is not in that form.
    /// </summary>
    internal static SnapObject? ParseMember(SnapObject package, string line)
    {
        var open = line.IndexOf('(');
        var space = line.IndexOf(' ');
        var nameEnd = open >= 0 && (space < 0 || open < space) ? open : space >= 0 ? space : line.Length;
        var name = line[..nameEnd];
        if (name.Length == 0) return null;
        var rest = line[nameEnd..];
        var parameters = new List<SnapParam>();
        if (rest.StartsWith('('))
        {
            var depth = 0;
            var close = -1;
            for (var i = 0; i < rest.Length && close < 0; i++)
            {
                if (rest[i] == '(') depth++;
                else if (rest[i] == ')' && --depth == 0) close = i;
            }
            if (close < 0) return null;
            var args = rest[1..close];
            rest = rest[(close + 1)..];
            var start = 0;
            depth = 0;
            for (var i = 0; i <= args.Length; i++)
            {
                if (i < args.Length && args[i] == '(') depth++;
                else if (i < args.Length && args[i] == ')') depth--;
                else if (i == args.Length || depth == 0 && args[i] == ',')
                {
                    var arg = args[start..i].Trim();
                    start = i + 1;
                    if (arg.Length == 0) continue;
                    var words = arg.Split(' ', 2);
                    var type = words.Length > 1 ? words[1] : "";
                    var output = false;
                    if (type.StartsWith("IN OUT ", StringComparison.Ordinal)) { type = type[7..]; output = true; }
                    else if (type.StartsWith("OUT ", StringComparison.Ordinal)) { type = type[4..]; output = true; }
                    else if (type.StartsWith("IN ", StringComparison.Ordinal)) type = type[3..];
                    parameters.Add(new SnapParam { Name = words[0], Type = (output ? (arg.Contains(" IN OUT ", StringComparison.Ordinal) ? "IN OUT " : "OUT ") : "") + type, Ordinal = parameters.Count + 1, Output = output });
                }
            }
        }
        var returns = rest.TrimStart().StartsWith("RETURN ", StringComparison.Ordinal) ? rest.TrimStart()[7..].Trim() : null;
        return new SnapObject
        {
            Id = MemberId, Schema = $"{Q(package.Schema)}.{Q(package.Name)}", Name = name, Kind = returns == null ? ObjectKind.Proc : ObjectKind.Func,
            ReturnType = returns, Params = parameters.ToArray(),
        };
    }
}
