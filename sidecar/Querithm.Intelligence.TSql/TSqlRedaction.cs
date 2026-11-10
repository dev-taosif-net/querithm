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
            ["SP_ADDLOGIN"] = ([1], []), ["SP_PASSWORD"] = ([0, 1], ["OLD", "NEW"]), ["SP_SETAPPROLE"] = ([1], []),
            ["SP_ADDAPPROLE"] = ([1], []), ["SP_APPROLEPASSWORD"] = ([1], ["NEWPWD"]), ["SP_ADDLINKEDSRVLOGIN"] = ([4], []),
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

    public static Redaction Redact(string text)
    {
        if (!SecretRedaction.MayContainSecrets(text, Rules)) return new Redaction(text, []);
        var list = new List<RedactToken>();
        var end = 0;
        foreach (var t in Parsers.Value!.GetTokenStream(new StringReader(text), out _))
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
        return SecretRedaction.Apply(text, list, Rules);
    }

    /// <summary>The text ScriptDom skipped between two tokens, when it opens a literal (<c>'</c> or <c>N'</c>).</summary>
    static string? UnclosedLiteral(string text, int from, int to)
    {
        if (from >= to) return null;
        var gap = text[from..to];
        return gap[0] == '\'' || (gap.Length > 1 && gap[0] is 'N' or 'n' && gap[1] == '\'') ? gap : null;
    }
}
