using Querithm.Contract.Providers;

namespace Querithm.Intelligence.PgSql;

/// <summary>
/// L4 on PostgreSQL tokens: <c>[ENCRYPTED] PASSWORD '…'</c> (CREATE / ALTER ROLE and USER), <c>password '…'</c> in
/// user-mapping and server options, a subscription's <c>CONNECTION '…'</c> (its conninfo carries the password), and the
/// pgcrypto functions' password arguments. DuckDB reuses the token mapping with its own rules.
/// </summary>
public static class PgRedaction
{
    static readonly SecretRules Rules = new()
    {
        SecretNames = new HashSet<string> { "CONNECTION" },
        Triggers = ["CONNECTION"],
        SecretArguments = new Dictionary<string, int[]>
        {
            ["CRYPT"] = [0], ["PGP_SYM_ENCRYPT"] = [1], ["PGP_SYM_DECRYPT"] = [1], ["PGP_SYM_ENCRYPT_BYTEA"] = [1], ["PGP_SYM_DECRYPT_BYTEA"] = [1],
        },
    };

    public static Redaction Redact(string text) =>
        SecretRedaction.MayContainSecrets(text, Rules) ? SecretRedaction.Apply(text, Tokens(PgTokenizer.Tokenize(text)), Rules) : new Redaction(text, []);

    /// <summary>The significant tokens as the redactor reads them (PostgreSQL and DuckDB share the lexer).</summary>
    public static List<RedactToken> Tokens(List<PgToken> tokens)
    {
        var list = new List<RedactToken>(tokens.Count);
        foreach (var t in tokens)
        {
            RedactTokenKind? kind = t.Kind switch
            {
                PgTokenKind.Whitespace or PgTokenKind.Comment => null,
                PgTokenKind.Word => RedactTokenKind.Word,
                PgTokenKind.QuotedIdent => RedactTokenKind.QuotedName,
                PgTokenKind.String or PgTokenKind.DollarString => RedactTokenKind.String,
                PgTokenKind.Number => RedactTokenKind.Number,
                PgTokenKind.Operator or PgTokenKind.Punct => RedactTokenKind.Punct,
                _ => RedactTokenKind.Other,
            };
            if (kind is not { } k) continue;
            var name = k switch
            {
                RedactTokenKind.Word => t.Text,
                RedactTokenKind.QuotedName when t.Text.Length >= 2 => t.Text[1..^1].Replace("\"\"", "\""),
                _ => "",
            };
            list.Add(new RedactToken(k, t.Offset, t.Length, t.Text, name));
        }
        return list;
    }
}
