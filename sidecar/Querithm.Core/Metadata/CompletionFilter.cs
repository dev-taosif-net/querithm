using Querithm.Contract;

namespace Querithm.Core.Metadata;

/// <summary>
/// Tier 2's answer as sent to the webview (§9): only items whose name matches the word being typed, at most
/// <see cref="MaxItems"/> in rank order. The dialect services list every object a context allows; on a 10,000-table schema
/// that was every table on every request. Matching is tier 1's (rank.ts): prefix, camel hump or substring, ignoring case,
/// on the label with its delimiters removed and on its last dotted part. Snippets (few, labelled by what they insert) pass.
/// </summary>
public static class CompletionFilter
{
    /// <summary>Tier 1's cap (complete.ts MAX_ITEMS): the editor shows no more, and asks again as the word grows.</summary>
    public const int MaxItems = 500;

    public static CompletionItem[] Apply(CompletionItem[] items, string text, int offset)
    {
        var prefix = WordBefore(text, offset);
        IEnumerable<CompletionItem> kept = prefix.Length == 0 ? items : items.Where(i => i.Kind == CompletionKind.Snippet || Matches(prefix, i.Label));
        if (prefix.Length == 0 && items.Length <= MaxItems) return items;
        var list = kept as CompletionItem[] ?? kept.ToArray();
        if (list.Length <= MaxItems) return list;
        return list.OrderBy(i => i.SortText ?? "￿", StringComparer.Ordinal).ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase).Take(MaxItems).ToArray();
    }

    /// <summary>The name being typed before <paramref name="offset"/>: letters, digits, _ $ # @ (an opening quote ends it).</summary>
    public static string WordBefore(string text, int offset)
    {
        var start = Math.Clamp(offset, 0, text.Length);
        var end = start;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        return text[start..end];
    }

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    static bool Matches(string prefix, string label)
    {
        var bare = Unquote(label);
        if (bare.Contains(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        var dot = bare.LastIndexOf('.');
        var last = dot >= 0 ? bare[(dot + 1)..] : bare;
        return Hump(prefix, last) || Hump(prefix, bare);
    }

    static string Unquote(string label)
    {
        Span<char> buf = label.Length <= 256 ? stackalloc char[label.Length] : new char[label.Length];
        var n = 0;
        foreach (var c in label) if (c is not ('[' or ']' or '"' or '`')) buf[n++] = c;
        return new string(buf[..n]);
    }

    /// <summary>Camel / underscore hump match (`oi` → OrderItems, `cId` → CustomerId); the first character must match.</summary>
    static bool Hump(string prefix, string cand)
    {
        if (prefix.Length == 0) return true;
        if (cand.Length < prefix.Length || char.ToLowerInvariant(prefix[0]) != char.ToLowerInvariant(cand[0])) return false;
        // Greedy: each typed character continues the run just matched or starts at a hump (an upper-case letter after a
        // lower-case one, or a letter / digit after a separator). Tier 1 backtracks; substring matching covers the rest.
        var pi = 1;
        var last = 0;
        for (var ci = 1; ci < cand.Length && pi < prefix.Length; ci++)
        {
            var c = cand[ci];
            var prev = cand[ci - 1];
            var hump = (char.IsUpper(c) && !char.IsUpper(prev)) || (char.IsLetterOrDigit(c) && !char.IsLetterOrDigit(prev));
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(prefix[pi]) && (hump || last == ci - 1)) { pi++; last = ci; }
        }
        return pi == prefix.Length;
    }
}
