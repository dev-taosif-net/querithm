using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.TSql;

/// <summary>
/// L4 on ScriptDom tokens: <c>PASSWORD = '…'</c> (CREATE / ALTER LOGIN, USER, APPLICATION ROLE; BACKUP / RESTORE;
/// ENCRYPTION / DECRYPTION BY PASSWORD), <c>OLD_PASSWORD</c>, <c>MEDIAPASSWORD</c>, a credential's <c>SECRET</c>, secret-named
/// variables and procedure parameters (<c>@rmtpassword = …</c>, <c>DECLARE @pwd nvarchar(50) = …</c>), the passphrase
/// functions and the positional password arguments of the security procedures.
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
        SecretProcedures = new Dictionary<string, (int[], string[])>
        {
            ["SP_ADDLOGIN"] = ([1], []), ["SP_PASSWORD"] = ([0, 1], ["OLD", "NEW"]), ["SP_SETAPPROLE"] = ([1], []),
            ["SP_ADDAPPROLE"] = ([1], []), ["SP_APPROLEPASSWORD"] = ([1], ["NEWPWD"]), ["SP_ADDLINKEDSRVLOGIN"] = ([4], []),
        },
    };

    public static Redaction Redact(string text)
    {
        if (!SecretRedaction.MayContainSecrets(text, Rules)) return new Redaction(text, []);
        var list = new List<RedactToken>();
        foreach (var t in Parsers.Value!.GetTokenStream(new StringReader(text), out _))
        {
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
            var name = k switch
            {
                RedactTokenKind.QuotedName when t.Text.Length >= 2 => t.Text[0] == '[' ? t.Text[1..^1].Replace("]]", "]") : t.Text[1..^1].Replace("\"\"", "\""),
                RedactTokenKind.Word or RedactTokenKind.Variable => t.Text,
                _ => "",
            };
            list.Add(new RedactToken(k, t.Offset, t.Text.Length, t.Text, name));
        }
        return SecretRedaction.Apply(text, list, Rules);
    }
}
