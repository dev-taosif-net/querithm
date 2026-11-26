using Querithm.Intelligence.TSql.Completion;

namespace Querithm.Tests;

/// <summary>
/// Review 2026-11-25 (B#3, Gemini #15): the T-SQL declaration parse cache. When one keystroke's declarations added up to
/// more than the budget, the cache was cleared partway through every keystroke and the next one started from the top, so
/// none of them hit: 40 declarations of 16 KB above the cursor took 888 ms per keystroke (median, Release, this machine;
/// 302 KB of them took 6.9 ms). Each test uses its own cache and clock: the shared one is used by every parallel test.
/// </summary>
public class CompletionCacheTests
{
    static string Decl(int i, int length) => ($"DECLARE @v{i:D3} int;" + new string(' ', length))[..length];

    [Fact]
    public void A_full_cache_keeps_the_entries_in_use_instead_of_starting_over()
    {
        long now = 0;
        var cache = new ParseCache(maxEntries: 1_000, maxChars: 10 * 100, maxText: 100, TimeSpan.FromSeconds(10), () => now);
        var texts = Enumerable.Range(0, 15).Select(i => Decl(i, 100)).ToArray(); // 15 declarations, room for 10
        var first = texts.Select(t => StatementParser.Parse(t, cache)).ToArray();
        now += 100;
        var second = texts.Select(t => StatementParser.Parse(t, cache)).ToArray();
        // The ten admitted first keep hitting on every keystroke; the other five parse each time and are not kept.
        Assert.Equal(10, Enumerable.Range(0, texts.Length).Count(i => ReferenceEquals(first[i], second[i])));
        Assert.Equal(1_000, cache.Chars);
        Assert.Equal(10, cache.Count);
    }

    [Fact]
    public void A_full_cache_of_entries_in_use_is_not_walked_on_every_miss()
    {
        // Review 2026-11-26: each miss of a full cache walked all its entries under the lock, finding nothing to drop.
        long now = 0;
        var cache = new ParseCache(maxEntries: 1_000, maxChars: 10 * 100, maxText: 100, TimeSpan.FromSeconds(10), () => now);
        var texts = Enumerable.Range(0, 15).Select(i => Decl(i, 100)).ToArray(); // 15 declarations, room for 10
        foreach (var t in texts) StatementParser.Parse(t, cache);
        now += 100;
        foreach (var t in texts) StatementParser.Parse(t, cache);
        Assert.Equal(1, cache.Sweeps); // 10 misses; nothing could be stale before the oldest entry's 10 s were up
        now += 10_000;
        foreach (var t in texts.Skip(10)) StatementParser.Parse(t, cache);
        Assert.Equal(2, cache.Sweeps);
    }

    [Fact]
    public void Entries_unused_for_a_while_make_room_for_new_texts()
    {
        long now = 0;
        var cache = new ParseCache(maxEntries: 1_000, maxChars: 3 * 100, maxText: 100, TimeSpan.FromSeconds(10), () => now);
        var old = Enumerable.Range(0, 3).Select(i => Decl(i, 100)).ToArray();
        foreach (var t in old) StatementParser.Parse(t, cache);
        now += 5_000;
        StatementParser.Parse(old[0], cache); // still in use
        now += 6_000;                         // old[1], old[2] unused for 11 s: edited away
        var edited = Decl(7, 100);
        StatementParser.Parse(edited, cache);
        Assert.Contains(edited, cache.Texts);
        Assert.Contains(old[0], cache.Texts);
        Assert.DoesNotContain(old[1], cache.Texts);
        Assert.Equal(200, cache.Chars);
    }

    [Fact]
    public void The_character_count_stays_exact_under_concurrent_parses()
    {
        // B#7: two threads could both pass the budget check and both add, so the count could pass the budget.
        var cache = new ParseCache(maxEntries: 1_000, maxChars: 20 * 100, maxText: 100, TimeSpan.FromSeconds(10), () => 0);
        var texts = Enumerable.Range(0, 200).Select(i => Decl(i, 100)).ToArray();
        Parallel.For(0, 2_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => StatementParser.Parse(texts[i % texts.Length], cache));
        Assert.Equal(cache.Texts.Sum(t => t.Length), cache.Chars);
        Assert.True(cache.Chars <= 2_000);
    }

    [Fact]
    public void A_text_longer_than_the_entry_limit_is_parsed_but_not_kept()
    {
        var cache = new ParseCache(maxEntries: 1_000, maxChars: 10_000, maxText: 100, TimeSpan.FromSeconds(10), () => 0);
        var big = Decl(1, 101);
        Assert.NotSame(StatementParser.Parse(big, cache), StatementParser.Parse(big, cache));
        Assert.Equal(0, cache.Count);
    }
}

/// <summary>
/// §14 #5e: T-SQL tier 2 with 40 <c>SELECT … INTO #t</c> declarations of 16 KB above the cursor (645 KB, past the
/// 512 Ki-character cache budget), in process, 20 keystrokes after 5 warm-ups, median. Wall-clock: the timing collection.
/// </summary>
[Collection("timing")]
public class CompletionCacheTimingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal static string Declarations()
    {
        var sb = new System.Text.StringBuilder("CREATE PROCEDURE dbo.p AS\nBEGIN\n");
        for (var t = 0; t < 40; t++)
        {
            sb.Append("SELECT ");
            for (var c = 0; c < 1_500; c++) sb.Append(c == 0 ? "" : ", ").Append($"o.Col{c:D4}");
            sb.Append($" INTO #t{t} FROM dbo.Orders o;\n");
        }
        return sb.ToString();
    }

    [Fact]
    [Trait("Category", "Measure")]
    public void Declarations_past_the_cache_budget_keep_most_of_their_parses()
    {
        var decls = Declarations();
        var schema = new FakeSchema(FakeSchema.SalesDb(), "SalesDB");
        // The cache is the process's: earlier tests' declarations, used within the last 10 s, fill it, and entries in use
        // are never dropped for a newcomer (by design), so this test's run depended on the test order (2026-11-26).
        StatementParser.ClearCache();
        var times = new List<double>();
        for (var i = 0; i < 25; i++)
        {
            var text = decls + "SELECT  FROM #t1" + new string(' ', i) + "\nEND";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TSqlCompletion.Complete(new Contract.CompleteParams
            {
                DocId = "decl-budget", Text = text, Offset = decls.Length + 7, Dialect = Contract.Dialect.Tsql, ProfileId = "p1", Database = "SalesDB",
            }, schema, CancellationToken.None);
            if (i >= 5) times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        output.WriteLine($"{decls.Length / 1024} KB of declarations: median {times[10]:F1} ms, p95 {times[18]:F1} ms per keystroke");
        // Before: every keystroke cleared the cache partway and parsed all 40 (888 ms median, Release).
        Assert.True(times[10] < TestTime.Budget(300), $"median {times[10]:F1} ms");
    }
}
