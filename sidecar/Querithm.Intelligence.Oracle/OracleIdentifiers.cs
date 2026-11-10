namespace Querithm.Intelligence.Oracle;

/// <summary>
/// Oracle identifier quoting and identity (D4 S02, D0e): an unquoted name folds to upper case (<c>emp</c> is <c>EMP</c>),
/// a quoted one is exact (<c>"emp"</c> is <c>emp</c>), and the data dictionary holds the folded spelling, so identity is
/// an ordinal comparison of <see cref="Identity(OracleToken)"/> values (<c>EngineTraits.NameMatch</c> is ordinal).
/// Oracle has no escape inside a quoted name: neither a quoted nor an unquoted identifier may hold <c>"</c> or NUL
/// (SQL Language Reference, Database Object Naming Rules), so <see cref="Quote"/> refuses such a name instead of
/// doubling the quote as other engines do.
/// </summary>
public static class OracleIdentifiers
{
    /// <summary>Oracle 12.2+ identifier limit, in bytes of the database character set (AL32UTF8 assumed: UTF-8 bytes).</summary>
    public const int MaxBytes = 128;

    /// <summary>Why <paramref name="name"/> cannot be an Oracle identifier at all; null when it can.</summary>
    public static string? Invalid(string name)
    {
        if (string.IsNullOrEmpty(name)) return "An Oracle name cannot be empty.";
        if (name.Contains('"') || name.Contains('\0')) return "An Oracle name cannot contain a double quote (\") or a NUL character.";
        if (System.Text.Encoding.UTF8.GetByteCount(name) > MaxBytes) return $"An Oracle name is at most {MaxBytes} bytes long.";
        return null;
    }

    /// <summary><c>"name"</c>, exact. Throws <see cref="ArgumentException"/> when the name cannot be an Oracle identifier.</summary>
    public static string Quote(string name) =>
        Invalid(name) is { } why ? throw new ArgumentException(why, nameof(name)) : "\"" + name + "\"";

    /// <summary>
    /// The name as it can be written bare (it is upper case, starts with a letter, holds only A–Z, 0–9, <c>_ $ #</c> and is
    /// not reserved), else quoted: writing either back reaches the same object.
    /// </summary>
    public static string QuoteIfNeeded(string name) => CanBeBare(name) ? name : Quote(name);

    public static bool CanBeBare(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] is not (>= 'A' and <= 'Z')) return false;
        foreach (var c in name)
            if (c is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '$' or '#')) return false;
        return !OracleKeywords.IsReserved(name) && name.Length <= MaxBytes;
    }

    /// <summary>The dictionary spelling a token names: a quoted name without its quotes, an unquoted one in upper case.</summary>
    public static string Identity(OracleToken t) => t.Kind switch
    {
        OracleTokenKind.QuotedIdent => Unquote(t.Text),
        OracleTokenKind.Bind => t.Text.Length > 1 && t.Text[1] == '"' ? Unquote(t.Text[1..]) : t.Text[1..].ToUpperInvariant(),
        _ => t.Text.ToUpperInvariant(),
    };

    /// <summary>The dictionary spelling of one name as typed (<c>emp</c> → <c>EMP</c>, <c>"emp"</c> → <c>emp</c>).</summary>
    public static string Identity(string typed)
    {
        var toks = OracleTokenizer.Tokenize(typed).Where(t => !t.IsTrivia).ToList();
        return toks.Count == 1 ? Identity(toks[0]) : typed;
    }

    static string Unquote(string text) => text.Length >= 2 && text[0] == '"' ? text[1..(text[^1] == '"' ? ^1 : ^0)] : text;
}
