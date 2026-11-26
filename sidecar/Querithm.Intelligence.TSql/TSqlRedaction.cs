using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.TSql;

/// <summary>
/// L4 on ScriptDom tokens: <c>PASSWORD = '…'</c> (CREATE / ALTER LOGIN, USER, APPLICATION ROLE; BACKUP / RESTORE;
/// ENCRYPTION / DECRYPTION BY PASSWORD), <c>OLD_PASSWORD</c>, <c>MEDIAPASSWORD</c>, a credential's <c>SECRET</c>, secret-named
/// variables and procedure parameters (<c>@rmtpassword = …</c>, <c>DECLARE @pwd nvarchar(50) = …</c>), the passphrase
/// functions, the positional password arguments of the security procedures, and the connection string of
/// <c>OPENROWSET</c> / <c>OPENDATASOURCE</c> (with OPENROWSET's <c>'source';'user';'password'</c> form).
/// </summary>
static class TSqlRedaction
{
    static readonly ThreadLocal<TSqlParser> Parsers = new(TSqlParsing.NewParser);

    static readonly SecretRules Rules = new()
    {
        SecretArguments = new Dictionary<string, int[]>
        {
            ["ENCRYPTBYPASSPHRASE"] = [0], ["DECRYPTBYPASSPHRASE"] = [0], ["PWDENCRYPT"] = [0], ["PWDCOMPARE"] = [0],
        },
        ConnectionArguments = new Dictionary<string, int[]> { ["OPENROWSET"] = [1], ["OPENDATASOURCE"] = [1] },
        Triggers = ["OPENROWSET", "OPENDATASOURCE"],
        Extra = OpenRowsetPassword,
        SecretProcedures = new Dictionary<string, (int[], string[])>
        {
            ["SP_ADDLOGIN"] = ([1], ["PASSWD"]), ["SP_PASSWORD"] = ([0, 1], ["OLD", "NEW"]), ["SP_SETAPPROLE"] = ([1], ["PASSWORD"]),
            ["SP_ADDAPPROLE"] = ([1], ["PASSWORD"]), ["SP_APPROLEPASSWORD"] = ([1], ["NEWPWD"]), ["SP_ADDLINKEDSRVLOGIN"] = ([4], ["RMTPASSWORD"]),
        },
    };

    /// <summary><c>OPENROWSET('provider', 'source';'user';'password', …)</c>: the third literal of the second argument.</summary>
    static int OpenRowsetPassword(IReadOnlyList<RedactToken> t, int i)
    {
        if (!t[i].Is("OPENROWSET") || i + 9 >= t.Count || !t[i + 1].IsP("(")) return -1;
        var k = i + 2;
        if (t[k].Kind != RedactTokenKind.String || !t[k + 1].IsP(",")) return -1;
        k += 2;
        return t[k].Kind == RedactTokenKind.String && t[k + 1].IsP(";") && t[k + 2].Kind == RedactTokenKind.String && t[k + 3].IsP(";")
            && t[k + 4].Kind == RedactTokenKind.String ? k + 4 : -1;
    }

    /// <summary>What history keeps of a text whose comments nest too deep to lex, so no secret in it could be found.</summary>
    internal const string TooDeepToCheck = "-- (not kept: this script nests comments too deeply to check it for passwords)";

    public static Redaction Redact(string text)
    {
        if (!SecretRedaction.MayContainSecrets(text, Rules)) return new Redaction(text, []);
        var list = new List<RedactToken>();
        var end = 0;
        // Comments nested too deep to lex: nothing in the text can be checked, so none of it is kept (L4).
        IList<TSqlParserToken>? tokens;
        try { tokens = TSqlParsing.Lex(Parsers.Value!, text, out _); }
        catch (InvalidOperationException) { tokens = null; } // the deep-stack thread was refused: nothing could be checked
        if (tokens == null) return new Redaction(TooDeepToCheck, []);
        foreach (var t in tokens)
        {
            // PT6-10: ScriptDom emits no token for an unclosed literal ('x or N'x up to the end: its lexer error), so the
            // text it skipped is that literal, still a string.
            if (t.Offset > end && UnclosedLiteral(text, end, Math.Min(t.Offset, text.Length)) is { } gap)
                list.Add(new RedactToken(RedactTokenKind.String, end, gap.Length, gap, ""));
            end = Math.Max(end, t.Offset + (t.Text?.Length ?? 0));
            var kind = t.TokenType switch
            {
                TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile => (RedactTokenKind?)null,
                TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral or TSqlTokenType.AsciiStringOrQuotedIdentifier or TSqlTokenType.HexLiteral => RedactTokenKind.String,
                TSqlTokenType.QuotedIdentifier => RedactTokenKind.QuotedName,
                TSqlTokenType.Variable => RedactTokenKind.Variable,
                TSqlTokenType.Integer or TSqlTokenType.Numeric or TSqlTokenType.Real or TSqlTokenType.Money => RedactTokenKind.Number,
                TSqlTokenType.EqualsSign or TSqlTokenType.Comma or TSqlTokenType.LeftParenthesis or TSqlTokenType.RightParenthesis or TSqlTokenType.Semicolon or TSqlTokenType.Dot => RedactTokenKind.Punct,
                // Identifiers and keywords alike: PASSWORD and SECRET are not reserved, BY and WITH are.
                _ when t.Text is { Length: > 0 } w && (char.IsLetter(w[0]) || w[0] == '_') => RedactTokenKind.Word,
                _ => RedactTokenKind.Other,
            };
            if (kind is not { } k) continue;
            var tx = t.Text ?? "";
            var name = k switch
            {
                RedactTokenKind.QuotedName when tx.Length >= 2 => tx[0] == '[' ? tx[1..^1].Replace("]]", "]") : tx[1..^1].Replace("\"\"", "\""),
                RedactTokenKind.Word or RedactTokenKind.Variable => tx,
                _ => "",
            };
            list.Add(new RedactToken(k, t.Offset, tx.Length, tx, name));
        }
        if (end < text.Length && UnclosedLiteral(text, end, text.Length) is { } tail) list.Add(new RedactToken(RedactTokenKind.String, end, tail.Length, tail, ""));
        NamesAsValues(list);
        return SecretRedaction.Apply(text, list, Rules);
    }

    /// <summary>
    /// T-SQL takes a bare or delimited name where a value goes (round-3 review 2026-11-26): <c>EXEC sp_addlogin bob, Summer2026</c>,
    /// <c>sp_password NULL, [N3w!Pass], 'sa'</c>, and under SET QUOTED_IDENTIFIER OFF <c>PASSWORD = "S3cret!"</c> (the lexer
    /// always reads <c>"…"</c> as a name). Such a token in a secret's place counts as its value: the shared rules mask strings.
    /// </summary>
    static void NamesAsValues(List<RedactToken> t)
    {
        static RedactToken AsValue(RedactToken x) => x with { Kind = RedactTokenKind.String };
        var quotedIdentifierOff = false;
        for (var i = 0; i < t.Count; i++)
        {
            // SET QUOTED_IDENTIFIER OFF: from there on "…" is a string (DECLARE @pwd … = "x", ENCRYPTBYPASSPHRASE("x", …)).
            if (t[i].Is("SET") && i + 2 < t.Count && t[i + 1].Is("QUOTED_IDENTIFIER")) quotedIdentifierOff = t[i + 2].Is("OFF");
            if (quotedIdentifierOff && t[i].Kind == RedactTokenKind.QuotedName && t[i].Text.StartsWith('"')) { t[i] = AsValue(t[i]); continue; }
            // name = "…"
            if (t[i].Kind == RedactTokenKind.QuotedName && t[i].Text.StartsWith('"') && i >= 2 && t[i - 1].IsP("=") && t[i - 2].Named
                && SecretRedaction.NamesSecret(t[i - 2].Name, Rules))
                t[i] = AsValue(t[i]);
            if (t[i].Kind is not (RedactTokenKind.Word or RedactTokenKind.QuotedName) || i + 1 < t.Count && (t[i + 1].IsP("(") || t[i + 1].IsP("."))
                || !Rules.SecretProcedures.TryGetValue(t[i].Name.ToUpperInvariant(), out var proc))
                continue;
            // The arguments of a secret procedure called without parentheses, up to ';' or the next statement's word.
            var position = 0;
            for (var k = i + 1; k < t.Count && !t[k].IsP(";");)
            {
                var named = k + 2 < t.Count && t[k].Kind == RedactTokenKind.Variable && t[k + 1].IsP("=");
                var v = named ? k + 2 : k;
                if (v >= t.Count) break;
                var single = v + 1 >= t.Count || t[v + 1].IsP(",") || t[v + 1].IsP(";") || t[v + 1].Kind == RedactTokenKind.Word && !t[v + 1].Is("OUTPUT");
                var secret = named
                    ? proc.Parameters.Contains(t[k].Name.TrimStart('@').ToUpperInvariant()) || SecretRedaction.NamesSecret(t[k].Name, Rules)
                    : proc.Positions.Contains(position);
                if (secret && single && (t[v].Kind == RedactTokenKind.QuotedName || t[v].Kind == RedactTokenKind.Word && !t[v].Is("NULL") && !t[v].Is("DEFAULT")))
                    t[v] = AsValue(t[v]);
                // On to the next argument at this level.
                var depth = 0;
                for (k = v; k < t.Count; k++)
                {
                    if (t[k].IsP("(")) depth++;
                    else if (t[k].IsP(")")) depth--;
                    else if (depth == 0 && (t[k].IsP(",") || t[k].IsP(";"))) break;
                    // A word after a value starts the next statement (T-SQL needs no ';').
                    else if (depth == 0 && k > v && t[k].Kind == RedactTokenKind.Word && !t[k].Is("OUTPUT") && !t[k].Is("OUT") && !t[k - 1].IsP("=")) break;
                }
                if (k >= t.Count || !t[k].IsP(",")) break;
                k++;
                position++;
            }
        }
    }

    /// <summary>The text ScriptDom skipped between two tokens, when it opens a literal (<c>'</c> or <c>N'</c>).</summary>
    static string? UnclosedLiteral(string text, int from, int to)
    {
        if (from >= to) return null;
        var gap = text[from..to];
        return gap[0] == '\'' || (gap.Length > 1 && gap[0] is 'N' or 'n' && gap[1] == '\'') ? gap : null;
    }
}
