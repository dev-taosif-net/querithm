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
        return message;
    }
}

/// <summary>An engine's secret-bearing syntax, on top of the rules every engine shares (<see cref="SecretRedaction"/>).</summary>
public sealed class SecretRules
{
    /// <summary>Extra names whose string value is a secret (DuckDB KEY_ID, TOKEN …); upper case.</summary>
    public IReadOnlySet<string> SecretNames { get; init; } = new HashSet<string>();
    /// <summary>Functions whose argument at each 0-based position is a secret when it is a string literal; upper case.</summary>
    public IReadOnlyDictionary<string, int[]> SecretArguments { get; init; } = new Dictionary<string, int[]>();
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
            if (t.Named && NamesSecret(t.Name, rules) && ValueAfterName(tokens, i) is var v and >= 0) secret.Add(v);
            if (t.Kind == RedactTokenKind.Word && i + 1 < tokens.Count && tokens[i + 1].IsP("(")
                && rules.SecretArguments.TryGetValue(t.Name.ToUpperInvariant(), out var positions))
                foreach (var a in Arguments(tokens, i + 2, ")")) if (positions.Contains(a.Position) && a.Single is { } s) secret.Add(s);
            if (t.Kind is RedactTokenKind.Word or RedactTokenKind.QuotedName && rules.SecretProcedures.TryGetValue(t.Name.ToUpperInvariant(), out var proc)
                && !(i + 1 < tokens.Count && (tokens[i + 1].IsP("(") || tokens[i + 1].IsP("."))))
                foreach (var a in Arguments(tokens, i + 1, null))
                    if ((a.Parameter == null ? proc.Positions.Contains(a.Position) : proc.Parameters.Contains(a.Parameter.TrimStart('@').ToUpperInvariant())) && a.Single is { } s)
                        secret.Add(s);
            if (rules.IdentifiedBy) Identified(tokens, i, secret);
            if (rules.Extra?.Invoke(tokens, i) is int x and >= 0) secret.Add(x);
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
        return j < tokens.Count && tokens[j].Kind == RedactTokenKind.String ? j : -1;
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
                    && k + 1 < tokens.Count && tokens[k + 1].Kind == RedactTokenKind.String)
                    secret.Add(k + 1);
            }
        }
        else if (t.Is("PASSWORD") && i > 0 && tokens[i - 1].Is("SET"))
        {
            for (var k = i + 1; k < tokens.Count && k <= i + 8; k++)
            {
                if (tokens[k].IsP(";")) break;
                if (tokens[k].IsP("=") && k + 1 < tokens.Count && tokens[k + 1].Kind == RedactTokenKind.String) { secret.Add(k + 1); break; }
            }
        }
    }

    readonly record struct Argument(int Position, string? Parameter, int? Single);

    /// <summary>
    /// The arguments from <paramref name="start"/>: comma-separated at depth 0, up to <paramref name="close"/> (a call) or,
    /// for a procedure called without parentheses, up to ";" or a word where no argument can continue. An argument that is
    /// one string literal (possibly after <c>@name =</c>) carries that token's index.
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
                yield return new Argument(position++, parameter, k - from == 1 && tokens[from].Kind == RedactTokenKind.String ? from : null);
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
