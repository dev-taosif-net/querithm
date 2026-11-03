using System.Collections.Concurrent;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querithm.Intelligence.TSql.Completion;

/// <summary>
/// ScriptDom parses of single statements, cached by exact text. Statements are small, so a parse is well under a
/// millisecond; the cache makes declarations above the cursor (DECLARE, CREATE TABLE #t, the module header) free on
/// every keystroke after the first.
/// </summary>
static class StatementParser
{
    public sealed record Parsed(TSqlScript? Script, int ErrorCount, int FirstErrorOffset)
    {
        public bool Ok => Script != null && ErrorCount == 0;
        public TSqlStatement? First => Script?.Batches.Count > 0 && Script.Batches[0].Statements.Count > 0 ? Script.Batches[0].Statements[0] : null;
    }

    static readonly ConcurrentDictionary<string, Parsed> Cache = new(StringComparer.Ordinal);
    [ThreadStatic] static TSqlParser? parser;

    public static Parsed Parse(string text)
    {
        if (Cache.TryGetValue(text, out var hit)) return hit;
        parser ??= TSqlParsing.NewParser();
        Parsed result;
        try
        {
            var script = parser.Parse(new StringReader(text), out var errors) as TSqlScript;
            result = new Parsed(script, errors.Count, errors.Count > 0 ? errors[0].Offset : -1);
        }
        catch
        {
            result = new Parsed(null, 1, 0);
        }
        if (Cache.Count > 4096) Cache.Clear();
        Cache[text] = result;
        return result;
    }
}
