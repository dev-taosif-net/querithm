using System.Text;

namespace Querithm.Contract.Providers;

/// <summary>What the redactor needs to know about one significant token (comments and whitespace are left out).</summary>
public enum RedactTokenKind { Word, QuotedName, Variable, String, Number, Punct, Other }

/// <summary>
/// One significant token of the engine's own lexer. <paramref name="Name"/> is a word's text, a quoted name without its
/// quotes, or a variable without its sigils; empty for the other kinds.
/// </summary>
public readonly record struct RedactToken(RedactTokenKind Kind, int Offset, int Length, string Text, string Name)
{
    public bool Is(string word) => Kind == RedactTokenKind.Word && string.Equals(Name, word, StringComparison.OrdinalIgnoreCase);
    public bool IsP(string punct) => Kind == RedactTokenKind.Punct && Text == punct;
    public bool Named => Kind is RedactTokenKind.Word or RedactTokenKind.QuotedName or RedactTokenKind.Variable;
}

/// <summary>Query text with the secret literals masked, and the literals' text (to mask them in an error message too).</summary>
public readonly record struct Redaction(string Text, IReadOnlyList<string> Secrets)
{
    /// <summary><paramref name="message"/> with every secret this redaction found replaced by <c>***</c>.</summary>
    public string? Mask(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        foreach (var s in Secrets) message = message.Replace(s, "***", StringComparison.Ordinal);
        foreach (var s in Secrets) if (s.Length > TruncatedPrefix) message = MaskTruncated(message, s);
        return message;
    }

    /// <summary>The shortest cut-off start of a secret <see cref="Mask"/> still masks (PT6-9).</summary>
    public const int TruncatedPrefix = 8;

    /// <summary>
    /// PT6-9: an engine error may quote a window of the statement that cuts the secret short (MySQL 1064's
    /// <c>near '%.80s'</c>): a start of <paramref name="secret"/>, at least <see cref="TruncatedPrefix"/> characters long,
    /// that runs to a quote or to the end of the message, is masked too.
    /// </summary>
    static string MaskTruncated(string message, string secret)
    {
        var head = secret[..TruncatedPrefix];
        var at = message.IndexOf(head, StringComparison.Ordinal);
        if (at < 0) return message;
        var sb = new StringBuilder();
        var from = 0;
        while (at >= 0)
        {
            var n = TruncatedPrefix;
            while (n < secret.Length && at + n < message.Length && message[at + n] == secret[n]) n++;
            var end = at + n;
            if (end == message.Length || message[end] is '\'' or '"' or '`')
            {
                sb.Append(message, from, at - from).Append("***");
                from = end;
            }
            at = message.IndexOf(head, Math.Max(end, at + 1), StringComparison.Ordinal);
        }
        return sb.Append(message, from, message.Length - from).ToString();
    }
}

/// <summary>An engine's secret-bearing syntax, on top of the rules every engine shares (<see cref="SecretRedaction"/>).</summary>
public sealed class SecretRules
{
    /// <summary>Extra names whose string value is a secret (DuckDB KEY_ID, TOKEN …); upper case.</summary>
    public IReadOnlySet<string> SecretNames { get; init; } = new HashSet<string>();
    /// <summary>Functions whose argument at each 0-based position is a secret when it is a string literal; upper case.</summary>
    public IReadOnlyDictionary<string, int[]> SecretArguments { get; init; } = new Dictionary<string, int[]>();
    /// <summary>
    /// Functions whose argument at each 0-based position is a connection string (OPENROWSET, dblink …): masked whole when it
    /// is a string literal that holds a credential (<see cref="SecretRedaction.HoldsCredential"/>); upper case.
    /// </summary>
    public IReadOnlyDictionary<string, int[]> ConnectionArguments { get; init; } = new Dictionary<string, int[]>();
    /// <summary>T-SQL system procedures called without parentheses: secret positional arguments and named parameters; upper case.</summary>
    public IReadOnlyDictionary<string, (int[] Positions, string[] Parameters)> SecretProcedures { get; init; } = new Dictionary<string, (int[], string[])>();
    /// <summary>MySQL / MariaDB: <c>IDENTIFIED [WITH plugin] BY | AS 'x'</c>, <c>IDENTIFIED VIA plugin USING 'x'</c>, <c>REPLACE 'old'</c>, and <c>SET PASSWORD FOR u = 'x'</c>.</summary>
    public bool IdentifiedBy { get; init; }
    /// <summary>
    /// Engine-specific rule over the significant tokens: returns the index of a secret string at <c>i</c>, or −1 (SQLite's
    /// <c>PRAGMA key = '…'</c>).
    /// </summary>
    public Func<IReadOnlyList<RedactToken>, int, int>? Extra { get; init; }
    /// <summary>Fragments the engine's extra rules fire on (with <see cref="SecretRedaction"/>'s own: see <see cref="SecretRedaction.Apply"/>).</summary>
    public string[] Triggers { get; init; } = [];
}

/// <summary>
/// L4: secrets typed in SQL (a password in ALTER LOGIN / CREATE ROLE / IDENTIFIED BY, a credential's or DuckDB secret's
/// key) become <c>&lt;redacted&gt;</c> before the text is kept anywhere — query history, the monitor's text store. It works on the
/// engine's own tokens, never on the raw text: a string literal is masked when a secret-naming word or variable stands
/// before it (with or without <c>=</c>), when it is a secret argument of a known function or procedure, or when the
/// engine's own rule says so. Everything else in the statement is kept as typed.
/// </summary>
public static class SecretRedaction
{
    /// <summary>
    /// What a secret literal becomes. Not a literal in any engine (8a review: <c>'***'</c> was one, so re-running
    /// <c>ALTER ROLE app PASSWORD '***'</c> from history set the password to <c>***</c>): <c>&lt;</c> cannot begin a
    /// value or an expression in T-SQL, PostgreSQL, MySQL / MariaDB, SQLite or DuckDB, so a re-run fails with a syntax error.
    /// </summary>
    public const string Mask = "<redacted>";

    /// <summary>Characters an engine's lexer may join with a following <c>&lt;</c> into one operator (PostgreSQL's <c>=&lt;</c>): the mask gets a space after them.</summary>
    const string OperatorChars = "=<>!~+-*/%^&|#@?:";

    /// <summary>
    /// Every rule fires on a token whose text contains one of these (case-insensitive), so a text with none of them has
    /// nothing to mask and is not tokenized at all — an exact shortcut, not a match: the decision is still made on tokens.
    /// </summary>
    static readonly string[] Triggers = ["PASS", "PWD", "SECRET", "PHRASE", "IDENTIFIED", "CRYPT", "PGP", "LOGIN", "APPROLE"];

    /// <summary>Quick check before tokenizing: false means no rule can fire.</summary>
    public static bool MayContainSecrets(string text, SecretRules rules)
    {
        foreach (var t in Triggers) if (text.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var t in rules.Triggers) if (text.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A name that holds a secret: PASSWORD, OLD_PASSWORD, MEDIAPASSWORD, @rmtpassword, @passwd, SECRET, PASSPHRASE, PWD …</summary>
    public static bool NamesSecret(string name, SecretRules rules)
    {
        var n = name.TrimStart('@').ToUpperInvariant();
        return n.Contains("PASSWORD", StringComparison.Ordinal) || n.Contains("PASSWD", StringComparison.Ordinal)
            || n.Contains("PASSPHRASE", StringComparison.Ordinal) || n.Contains("SECRET", StringComparison.Ordinal)
            || n is "PWD" || n.StartsWith("PWD_", StringComparison.Ordinal) || n.EndsWith("_PWD", StringComparison.Ordinal)
            || rules.SecretNames.Contains(n);
    }

    public static Redaction Apply(string text, IReadOnlyList<RedactToken> tokens, SecretRules rules)
    {
        var secret = new SortedSet<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Named && NamesSecret(t.Name, rules) && ValueAfterName(tokens, i) is var v and >= 0) AddRun(tokens, v, secret);
            if (t.Kind == RedactTokenKind.Word && i + 1 < tokens.Count && tokens[i + 1].IsP("(")
                && rules.SecretArguments.TryGetValue(t.Name.ToUpperInvariant(), out var positions))
                foreach (var a in Arguments(tokens, i + 2, ")")) if (positions.Contains(a.Position) && a.Single is { } s) AddRun(tokens, s, secret);
            if (t.Kind == RedactTokenKind.Word && i + 1 < tokens.Count && tokens[i + 1].IsP("(")
                && rules.ConnectionArguments.TryGetValue(t.Name.ToUpperInvariant(), out var connections))
                foreach (var a in Arguments(tokens, i + 2, ")"))
                    if (connections.Contains(a.Position) && a.Single is { } s && Run(tokens, s).Any(k => HoldsCredential(tokens[k].Text))) AddRun(tokens, s, secret);
            if (t.Kind is RedactTokenKind.Word or RedactTokenKind.QuotedName && rules.SecretProcedures.TryGetValue(t.Name.ToUpperInvariant(), out var proc)
                && !(i + 1 < tokens.Count && (tokens[i + 1].IsP("(") || tokens[i + 1].IsP("."))))
                foreach (var a in Arguments(tokens, i + 1, null))
                    if ((a.Parameter == null ? proc.Positions.Contains(a.Position) : proc.Parameters.Contains(a.Parameter.TrimStart('@').ToUpperInvariant())) && a.Single is { } s)
                        AddRun(tokens, s, secret);
            if (rules.IdentifiedBy) Identified(tokens, i, secret);
            if (rules.Extra?.Invoke(tokens, i) is int x and >= 0) AddRun(tokens, x, secret);
        }
        if (secret.Count == 0) return new Redaction(text, []);
        var sb = new StringBuilder(text);
        var values = new List<string>();
        foreach (var k in secret.Reverse())
        {
            var t = tokens[k];
            if (t.Kind != RedactTokenKind.String) continue;
            values.AddRange(Inner(t.Text));
            // The whole token goes, a prefix too (N'…', E'…', X'…', $$…$$): what is left must not be a literal.
            sb.Remove(t.Offset, t.Length).Insert(t.Offset, t.Offset > 0 && OperatorChars.Contains(text[t.Offset - 1]) ? " " + Mask : Mask);
        }
        return new Redaction(sb.ToString(), values.Where(v => v.Length >= 3).Distinct().OrderByDescending(v => v.Length).ToArray());
    }

    /// <summary>
    /// A connection-string literal that carries a credential: a password / pwd keyword, or a URI's <c>user:password@</c>.
    /// A check on one token's own text, after the lexer said it is a string.
    /// </summary>
    public static bool HoldsCredential(string literal) =>
        literal.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) || literal.Contains("PWD", StringComparison.OrdinalIgnoreCase)
        || (literal.Contains("://", StringComparison.Ordinal) && literal.Contains('@'));

    /// <summary>
    /// PT6-11: a literal and the ones right after it, which the engine joins into one value (MySQL <c>'a' 'b'</c>,
    /// PostgreSQL <c>'a'</c> newline <c>'b'</c>): all of them are the secret.
    /// </summary>
    static IEnumerable<int> Run(IReadOnlyList<RedactToken> tokens, int first)
    {
        for (var k = first; k < tokens.Count && tokens[k].Kind == RedactTokenKind.String; k++) yield return k;
    }

    static void AddRun(IReadOnlyList<RedactToken> tokens, int first, SortedSet<int> secret)
    {
        foreach (var k in Run(tokens, first)) secret.Add(k);
    }

    /// <summary>
    /// The literal at <paramref name="j"/>, after a MySQL / MariaDB character-set introducer (<c>_utf8mb4'x'</c>, PT6-11):
    /// its index, or −1.
    /// </summary>
    static int LiteralAt(IReadOnlyList<RedactToken> tokens, int j)
    {
        if (j + 1 < tokens.Count && tokens[j].Kind == RedactTokenKind.Word && tokens[j].Name is { Length: > 1 } w && w[0] == '_'
            && tokens[j + 1].Kind == RedactTokenKind.String)
            j++;
        return j < tokens.Count && tokens[j].Kind == RedactTokenKind.String ? j : -1;
    }

    /// <summary>The string after a secret's name: <c>name 'x'</c>, <c>name = 'x'</c>, <c>name := 'x'</c>, or (a declared variable) <c>@v type(n) = 'x'</c>.</summary>
    static int ValueAfterName(IReadOnlyList<RedactToken> tokens, int i)
    {
        var j = i + 1;
        if (j < tokens.Count && (tokens[j].IsP("=") || tokens[j].IsP(":="))) j++;
        else if (tokens[i].Kind == RedactTokenKind.Variable)
        {
            // DECLARE @pwd [AS] nvarchar(100) = N'…': skip the type (words and one parenthesized size) up to "=".
            var k = j;
            for (var steps = 0; k < tokens.Count && steps < 8; steps++, k++)
            {
                if (tokens[k].IsP("=")) { j = k + 1; break; }
                if (tokens[k].IsP("(")) { while (k < tokens.Count && !tokens[k].IsP(")")) k++; continue; }
                if (tokens[k].Kind != RedactTokenKind.Word) break;
            }
        }
        return LiteralAt(tokens, j);
    }

    /// <summary>MySQL / MariaDB account syntax: the string after BY / AS / USING / REPLACE within the IDENTIFIED clause, and SET PASSWORD [FOR u] = 'x'.</summary>
    static void Identified(IReadOnlyList<RedactToken> tokens, int i, SortedSet<int> secret)
    {
        var t = tokens[i];
        if (t.Is("IDENTIFIED"))
        {
            for (var k = i + 1; k < tokens.Count && k <= i + 7; k++)
            {
                if (tokens[k].IsP(",") || tokens[k].IsP(";")) break;
                if ((tokens[k].Is("BY") || tokens[k].Is("AS") || tokens[k].Is("USING") || tokens[k].Is("REPLACE"))
                    && LiteralAt(tokens, k + 1) is var v and >= 0)
                    AddRun(tokens, v, secret);
            }
        }
        else if (t.Is("PASSWORD") && i > 0 && tokens[i - 1].Is("SET"))
        {
            for (var k = i + 1; k < tokens.Count && k <= i + 8; k++)
            {
                if (tokens[k].IsP(";")) break;
                if (tokens[k].IsP("=") && LiteralAt(tokens, k + 1) is var v and >= 0) { AddRun(tokens, v, secret); break; }
            }
        }
    }

    readonly record struct Argument(int Position, string? Parameter, int? Single);

    /// <summary>
    /// The arguments from <paramref name="start"/>: comma-separated at depth 0, up to <paramref name="close"/> (a call) or,
    /// for a procedure called without parentheses, up to ";" or a word where no argument can continue. An argument that is
    /// string literal (possibly after <c>@name =</c>), or adjacent literals the engine joins, carries the first one's index.
    /// </summary>
    static IEnumerable<Argument> Arguments(IReadOnlyList<RedactToken> tokens, int start, string? close)
    {
        var position = 0;
        var begin = start;
        var depth = 0;
        for (var k = start; k <= tokens.Count; k++)
        {
            var end = k == tokens.Count || (depth == 0 && (close != null ? tokens[k].IsP(close) : tokens[k].IsP(";")));
            if (!end && close == null && depth == 0 && k > begin && tokens[k].Kind == RedactTokenKind.Word && !tokens[k].Is("OUTPUT") && !tokens[k].Is("OUT")
                && !tokens[k - 1].IsP("=") && !tokens[k - 1].IsP(",")) end = true; // the next statement starts
            if (end || (depth == 0 && tokens[k].IsP(",")))
            {
                string? parameter = null;
                var from = begin;
                if (k - begin >= 2 && tokens[begin].Kind == RedactTokenKind.Variable && tokens[begin + 1].IsP("=")) { parameter = tokens[begin].Name; from = begin + 2; }
                var literals = k > from;
                for (var m = from; m < k && literals; m++) literals = tokens[m].Kind == RedactTokenKind.String;
                yield return new Argument(position++, parameter, literals ? from : null);
                if (end) yield break;
                begin = k + 1;
                continue;
            }
            if (tokens[k].IsP("(")) depth++;
            else if (tokens[k].IsP(")")) depth--;
            if (depth < 0) yield break;
        }
    }

    /// <summary>A literal's text between its quotes, as typed and with doubled quotes undone (servers echo either in errors).</summary>
    static IEnumerable<string> Inner(string literal)
    {
        var open = literal.IndexOfAny(['\'', '"']);
        if (open < 0 || literal.Length - open < 2) yield break;
        var q = literal[open];
        var close = literal[^1] == q ? literal.Length - 1 : literal.Length;
        var raw = literal[(open + 1)..close];
        yield return raw;
        var doubled = new string(q, 2);
        if (raw.Contains(doubled, StringComparison.Ordinal)) yield return raw.Replace(doubled, q.ToString(), StringComparison.Ordinal);
    }
}
