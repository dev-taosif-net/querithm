using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Querithm.Contract;

namespace Querithm.Intelligence.PgSql.Completion;

/// <summary>A non-trivia token with its offset in the whole document (dollar-quoted bodies are tokenized in place).</summary>
readonly record struct Tok(PgTokenKind Kind, int Offset, int Length, string Text)
{
    public int End => Offset + Length;
    public bool IsIdent => Kind is PgTokenKind.Word or PgTokenKind.QuotedIdent;
    /// <summary>The identifier as the dialect resolves it: quoted is exact; unquoted folds to lower case on PostgreSQL.</summary>
    public string Name => Kind == PgTokenKind.QuotedIdent ? PgIdent.Unquote(Text) : PgCompletionRules.Current.FoldsUnquoted ? Text.ToLowerInvariant() : Text;
    public bool Is(string word) => Kind == PgTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    public bool IsP(string p) => Kind is PgTokenKind.Punct or PgTokenKind.Operator && Text == p;
}

/// <summary>Identifier quoting the way the dialect reads names back (PostgreSQL: reserved words and anything not lower case get quotes).</summary>
static class PgIdent
{
    public static string Unquote(string t) => PgCompletionRules.Current.Unquote(t);

    public static string UnquoteDoubleQuoted(string t) =>
        t.Length >= 2 && t[^1] == '"' ? t[1..^1].Replace("\"\"", "\"") : t.TrimStart('"').Replace("\"\"", "\"");

    public static string Quote(string s) => PgCompletionRules.Current.Quote(s);
}

/// <summary>
/// One document's tokens, cached per docId while the text is unchanged: the outer statements, each dollar-quoted body
/// (tokenized on first use), and the temp tables the script creates. Nothing here ever throws on malformed text.
/// </summary>
sealed class PgDocument
{
    static readonly ConcurrentDictionary<string, PgDocument> Cache = new(StringComparer.Ordinal);

    public PgCompletionRules Rules { get; }

    public string Text { get; }
    public List<PgToken> Raw { get; }
    readonly ConcurrentDictionary<int, List<PgToken>> _bodies = new();
    List<TempTable>? _temps;
    List<List<PgToken>>? _statements;

    /// <summary>
    /// The outer statements, split once per document: re-splitting a 10,000-statement script on every completion call cost
    /// 162 ms of a 182 ms median (Stopwatch profile, bench #5.sqlite, 2026-10-31).
    /// </summary>
    public List<List<PgToken>> Statements => _statements ??= Rules.Statements(Text, Raw);

    PgDocument(string text, PgCompletionRules rules)
    {
        Text = text;
        Rules = rules;
        Raw = rules.Tokenize(text);
    }

    public static PgDocument Get(string text, string docId)
    {
        var rules = PgCompletionRules.Current;
        var key = rules.Id + "|" + docId;
        if (Cache.TryGetValue(key, out var d) && string.Equals(d.Text, text, StringComparison.Ordinal)) return d;
        d = new PgDocument(text, rules);
        if (Cache.Count > 64) Cache.Clear();
        Cache[key] = d;
        return d;
    }

    /// <summary>M12: drops the document under every dialect's key (rules.Id|docId).</summary>
    internal static void Forget(string docId)
    {
        var suffix = "|" + docId;
        foreach (var key in Cache.Keys)
            if (key.EndsWith(suffix, StringComparison.Ordinal)) Cache.TryRemove(key, out _);
    }

    internal static bool IsCached(string docId) => Cache.Keys.Any(k => k.EndsWith("|" + docId, StringComparison.Ordinal));

    /// <summary>Length of the opening $tag$ of a dollar-quoted token.</summary>
    public static int TagLength(PgToken t) => t.Text.IndexOf('$', 1) + 1;

    /// <summary>A body still being typed has no closing tag: it runs to the end of the document.</summary>
    public static bool Terminated(PgToken t)
    {
        var tag = TagLength(t);
        return t.Text.Length >= 2 * tag && t.Text.EndsWith(t.Text[..tag], StringComparison.Ordinal);
    }

    /// <summary>The raw tokens of a dollar-quoted body, with absolute offsets.</summary>
    public List<PgToken> Body(PgToken dollar) => _bodies.GetOrAdd(dollar.Offset, _ =>
    {
        var tag = TagLength(dollar);
        var inner = Terminated(dollar) ? dollar.Text[tag..^tag] : dollar.Text[tag..];
        var shift = dollar.Offset + tag;
        var list = Rules.Tokenize(inner);
        for (var i = 0; i < list.Count; i++) list[i] = list[i] with { Offset = list[i].Offset + shift };
        return list;
    });

    readonly ConcurrentDictionary<List<PgToken>, List<Tok>> _code = new(ReferenceEqualityComparer.Instance);

    /// <summary>The code tokens of the document or of one of its bodies (raw lists from <see cref="Raw"/> / <see cref="Body"/>), converted once.</summary>
    public List<Tok> CodeOf(List<PgToken> raw) => _code.GetOrAdd(raw, Code);

    /// <summary>A dollar string that holds code: after AS (a routine) or DO (an anonymous block), where the dialect has such bodies.</summary>
    public static bool IsBody(List<PgToken> raw, int index)
    {
        if (!PgCompletionRules.Current.DollarBodiesAreCode) return false;
        var seen = 0;
        for (var i = index - 1; i >= 0 && seen < 3; i--)
        {
            if (raw[i].IsTrivia) continue;
            seen++;
            if (raw[i].Is("AS") && seen == 1) return true;
            if (raw[i].Is("DO")) return true; // DO $$ / DO LANGUAGE plpgsql $$
        }
        return false;
    }

    public static List<Tok> Code(IEnumerable<PgToken> raw) =>
        raw.Where(t => !t.IsTrivia).Select(t => new Tok(t.Kind, t.Offset, t.Length, t.Text)).ToList();

    /// <summary>CREATE TEMP TABLE statements anywhere in the script (outer text and routine bodies), in text order.</summary>
    public List<TempTable> TempTables => _temps ??= ScanTemps();

    List<TempTable> ScanTemps()
    {
        var list = new List<TempTable>();
        void Scan(List<PgToken> raw)
        {
            var code = CodeOf(raw);
            for (var i = 0; i + 3 < code.Count; i++)
            {
                if (!code[i].Is("CREATE")) continue;
                var j = i + 1;
                if (code[j].Is("GLOBAL") || code[j].Is("LOCAL")) j++;
                if (j >= code.Count || !(code[j].Is("TEMP") || code[j].Is("TEMPORARY"))) continue;
                j++;
                if (j < code.Count && code[j].Is("UNLOGGED")) j++;
                if (j >= code.Count || !code[j].Is("TABLE")) continue;
                j++;
                if (j + 2 < code.Count && code[j].Is("IF") && code[j + 1].Is("NOT") && code[j + 2].Is("EXISTS")) j += 3;
                if (j >= code.Count || !code[j].IsIdent) continue;
                // pg_temp.name is the same table.
                if (j + 2 < code.Count && code[j + 1].IsP(".") && code[j + 2].IsIdent) j += 2;
                var name = code[j].Name;
                var end = j + 1;
                for (var depth = 0; end < code.Count && !(depth == 0 && code[end].IsP(";")); end++)
                    depth += code[end].IsP("(") ? 1 : code[end].IsP(")") ? -1 : 0;
                list.Add(new TempTable(name, code[i].Offset, code.GetRange(j + 1, end - j - 1)));
                i = end;
            }
            for (var i = 0; i < raw.Count; i++)
                if (raw[i].Kind == PgTokenKind.DollarString && IsBody(raw, i)) Scan(Body(raw[i]));
        }
        Scan(Raw);
        return list;
    }
}

/// <summary>A temp table the script creates: its definition tokens after the name (column list, LIKE, or AS query).</summary>
sealed record TempTable(string Name, int Offset, List<Tok> Definition);

/// <summary>A column as completion shows it.</summary>
sealed record Col(string Name, string? Type);

/// <summary>Name lookups over one snapshot (PostgreSQL names are exact), built once per snapshot instance.</summary>
sealed class PgSchema
{
    public required SchemaSnapshot Snapshot { get; init; }
    public required Dictionary<string, List<SnapObject>> ByName { get; init; }
    public required HashSet<string> Schemas { get; init; }
    public required StringComparer Names { get; init; }
    public required string DefaultSchema { get; init; }

    public static PgSchema? For(SchemaSnapshot? snap) => snap == null ? null : PgCompletionRules.Current.Schemas.GetValue(snap, s =>
    {
        var names = PgCompletionRules.Current.Names;
        var byName = new Dictionary<string, List<SnapObject>>(names);
        foreach (var o in s.Objects.Concat(PgCompletionRules.Current.BuiltinRoutines))
        {
            if (o.Kind == ObjectKind.Trigger) continue; // never named in queries
            if (!byName.TryGetValue(o.Name, out var list)) byName[o.Name] = list = [];
            list.Add(o);
        }
        return new PgSchema { Snapshot = s, ByName = byName, Schemas = s.Objects.Select(o => o.Schema).ToHashSet(names), Names = names, DefaultSchema = PgCompletionRules.Current.DefaultSchema };
    });

    /// <summary>schema.name, or name resolved on the default search path (public first, else the only schema that has it).</summary>
    public SnapObject? Find(string? schema, string name, Func<SnapObject, bool> kind)
    {
        if (!ByName.TryGetValue(name, out var list)) return null;
        var hits = list.Where(kind).ToList();
        if (schema != null) return hits.FirstOrDefault(o => Names.Equals(o.Schema, schema));
        return hits.FirstOrDefault(o => Names.Equals(o.Schema, DefaultSchema)) ?? (hits.Count == 1 ? hits[0] : hits.FirstOrDefault(o => o.Schema != "pg_catalog"));
    }

    public IEnumerable<SnapObject> Routines(string? schema, string name) =>
        ByName.TryGetValue(name, out var list) ? list.Where(o => o.Kind is ObjectKind.Func or ObjectKind.Tvf or ObjectKind.Proc && (schema == null || Names.Equals(o.Schema, schema))) : [];

    public static bool IsRelation(SnapObject o) => o.Kind is ObjectKind.Table or ObjectKind.View;
}
