using System.Globalization;
using System.Text;
using Querithm.Contract;
using Querithm.Core.Sessions;

namespace Querithm.Core.Store;

/// <summary>
/// The one rule for connection group names (§13 PT8-3). Only the sidecar cleans and matches names: "Payments",
/// "payments " and "PAYMENTS" are one group, and every profile and group it returns carries that group's one spelling.
/// </summary>
public static class GroupNames
{
    /// <summary>
    /// The longest raw text looked at. It is checked before anything is normalized: a wire message may hold megabytes,
    /// and NFKC expands some characters (U+FDFA) about 18 times.
    /// </summary>
    public const int MaxRawLength = 1024;

    const string Refusal =
        "The group name must be at most 256 characters, without line breaks, control characters or invisible formatting characters.";

    /// <summary>
    /// Null when <paramref name="raw"/> is usable, else the refusal. <paramref name="name"/> is the cleaned name: trimmed,
    /// every run of white space one space, NFC; null when nothing is left ("no group").
    /// </summary>
    public static ProfileProblem? Clean(string? raw, out string? name, string field = "groupName")
    {
        name = null;
        if (string.IsNullOrEmpty(raw)) return null;
        // Bounded and free of broken UTF-16 before Normalize can throw or allocate (G3).
        if (raw.Length > MaxRawLength || !ProfileRules.CleanText(raw, visible: true)) return new(Refusal, field);
        var collapsed = Collapse(raw);
        if (collapsed.Length == 0) return null;
        var nfc = collapsed.Normalize(NormalizationForm.FormC);
        if (nfc.Length > GroupLimits.MaxNameLength) return new(Refusal, field);
        if (!HasVisibleBase(nfc)) return new("The group name needs at least one visible letter, digit or symbol.", field);
        name = nfc;
        return null;
    }

    /// <summary>The cleaned name, or null when <paramref name="raw"/> is empty or breaks the rules.</summary>
    public static string? CleanOrNull(string? raw) => Clean(raw, out var name) == null ? name : null;

    /// <summary>What two names must share to be one group: NFKC, then simple upper-casing per code point.</summary>
    public static string Key(string name)
    {
        var kc = name.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(kc.Length);
        foreach (var rune in kc.EnumerateRunes()) sb.Append(Rune.ToUpperInvariant(rune).ToString());
        return sb.ToString();
    }

    /// <summary>The key of a name a caller looks up (rename's From, delete's Name); null when it cannot name any group.</summary>
    public static string? LookupKey(string? raw) => CleanOrNull(raw) is { } name ? Key(name) : null;

    /// <summary>
    /// One entry per group: a stored row's name, otherwise the spelling most profiles carry (ties: the ordinally smallest),
    /// with its member count. Names that break the rules (written before they were checked) are never a group;
    /// <paramref name="skipped"/> counts the profiles that carry one.
    /// </summary>
    public static Dictionary<string, GroupEntry> Resolve(IEnumerable<string> rowNames, IEnumerable<(string Raw, int Count)> profileNames, out int skipped)
    {
        skipped = 0;
        var members = new Dictionary<string, int>(StringComparer.Ordinal);
        var spellings = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (raw, count) in profileNames)
        {
            if (CleanOrNull(raw) is not { } name)
            {
                skipped += count;
                continue;
            }
            var key = Key(name);
            members[key] = members.GetValueOrDefault(key) + count;
            if (!spellings.TryGetValue(key, out var forKey)) spellings[key] = forKey = new(StringComparer.Ordinal);
            forKey[name] = forKey.GetValueOrDefault(name) + count;
        }
        var result = new Dictionary<string, GroupEntry>(StringComparer.Ordinal);
        foreach (var row in rowNames)
        {
            if (CleanOrNull(row) is not { } name) continue;
            var key = Key(name);
            result.TryAdd(key, new GroupEntry(name, members.GetValueOrDefault(key), Stored: true));
        }
        foreach (var (key, forKey) in spellings)
        {
            if (result.ContainsKey(key)) continue;
            var best = forKey.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            result[key] = new GroupEntry(best, members[key], Stored: false);
        }
        return result;
    }

    static string Collapse(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var space = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Hangul and Braille fillers draw as nothing, like spaces and lone combining marks.</summary>
    static readonly HashSet<int> Fillers = [0x115F, 0x1160, 0x3164, 0xFFA0, 0x2800];

    static bool HasVisibleBase(string name)
    {
        foreach (var rune in name.EnumerateRunes())
        {
            if (Fillers.Contains(rune.Value)) continue;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.SpaceSeparator) continue;
            return true;
        }
        return false;
    }
}

/// <summary>A group as <see cref="GroupNames.Resolve"/> sees it.</summary>
public readonly record struct GroupEntry(string Name, int Members, bool Stored);
