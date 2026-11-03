using System.Text;

namespace Querithm.Contract.Providers;

/// <summary>
/// Text placed inside a comment of a generated script (L6): object names, labels, file names, warnings — anything a
/// server, a file or the user chose. A line break ends a <c>--</c> comment, so the rest of the name would run as SQL;
/// <c>*/</c> ends a block comment and <c>/*</c> opens a nested one (PostgreSQL and SQL Server nest them), so the comment
/// would end early or swallow the code after it. Every such interpolation goes through <see cref="Safe"/>.
/// </summary>
public static class CommentText
{
    /// <summary>
    /// The text on one line, safe in <c>-- …</c> and <c>/* … */</c>: CR, LF, NEL (U+0085), the Unicode line and paragraph
    /// separators and the other control characters (tab kept) become spaces, and a space goes between <c>*</c> and
    /// <c>/</c> in either order, so neither <c>*/</c> nor <c>/*</c> can appear.
    /// </summary>
    public static string Safe(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        StringBuilder? sb = null;
        var prev = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var replaced = ch is '\u0085' or '\u2028' or '\u2029' || (char.IsControl(ch) && ch != '\t') ? ' ' : ch;
            var split = (prev == '*' && replaced == '/') || (prev == '/' && replaced == '*');
            if (sb == null && (replaced != ch || split))
                sb = new StringBuilder(text.Length + 8).Append(text, 0, i);
            if (sb != null)
            {
                if (split) sb.Append(' ');
                sb.Append(replaced);
            }
            prev = replaced;
        }
        return sb?.ToString() ?? text;
    }
}
