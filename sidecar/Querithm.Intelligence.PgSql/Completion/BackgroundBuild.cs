using System.Collections.Concurrent;

namespace Querithm.Intelligence.PgSql.Completion;

/// <summary>
/// Code audit 2026-11-25 (M6): the tokenizers stop at the request's token, and a tier-2 request ends at its 3 s deadline
/// (ObjectService's LanguageService). A 16 MB document took 2.1–8.8 s to read cold (Release build on the dev machine,
/// 2026-11-26: PostgreSQL 2.1, Oracle 4.8, MySQL 5.3, SQLite 6.2, DuckDB 8.8 s), so it was never cached and every request
/// on it ran 3 s and was cancelled. A long document (and a whole-document scan, <see cref="DocumentScan{T}"/>) is now read
/// once on a pool thread of its own: a newer text for the same key, or lang.forget, ends that read; the requests only wait
/// for it, each within its own deadline, and once it is stored every request on that text answers from it.
/// </summary>
static class BackgroundBuild
{
    /// <summary>A text this long is read in the background; a shorter one on the request (well under the deadline cold).</summary>
    internal const int MinChars = 2_000_000;

    sealed class Build(string text)
    {
        public string Text { get; } = text;
        public CancellationTokenSource Cts { get; } = new();
        /// <summary>Set when the read ends (fourth round, M1: waiters no longer spin for a Task field set a moment later).</summary>
        public TaskCompletionSource<object?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>At most this many reads at once (fourth round, M1): each holds a pool thread and a long text's tokens.</summary>
    static readonly SemaphoreSlim Slots = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    static readonly ConcurrentDictionary<string, Build> Running = new(StringComparer.Ordinal);

    /// <summary>Reads running in the background (tests).</summary>
    internal static int Count => Running.Count;

    /// <summary>
    /// The value <paramref name="make"/> reads from <paramref name="text"/>, waited for within the running request's token
    /// (<see cref="Tier2Cancellation.Current"/>). <paramref name="store"/> caches it once it is read, unless a newer text or
    /// a forget replaced the read meanwhile, and <paramref name="unstore"/> takes it out again when that happens between
    /// the check and the store; <paramref name="release"/> lets go of what the read kept on its thread.
    /// </summary>
    internal static T Run<T>(string key, string text, Func<T> make, Action<T> store, Action<T> unstore, Action? release = null)
    {
        Build? started = null;
        var build = Running.AddOrUpdate(key,
            _ => started = new Build(text),
            (_, older) =>
            {
                if (string.Equals(older.Text, text, StringComparison.Ordinal)) return older;
                Cancel(older); // a newer text: the older read is of no use
                return started = new Build(text);
            });
        if (ReferenceEquals(build, started))
        {
            var token = build.Cts.Token;
            _ = Task.Run(() =>
            {
                var slot = false;
                try
                {
                    Slots.Wait(token);
                    slot = true;
                    using var cancel = Tier2Cancellation.Use(token);
                    var value = make();
                    token.ThrowIfCancellationRequested();
                    if (IsCurrent(key, build))
                    {
                        store(value);
                        // Second round (L): a forget between the check and the store left the document cached after it.
                        // Forget takes the read out of Running before it clears the caches, so a store that no longer
                        // finds it is undone; one that still finds it is cleared by that forget.
                        if (!IsCurrent(key, build)) unstore(value);
                    }
                    build.Done.TrySetResult(value);
                }
                catch (OperationCanceledException) { build.Done.TrySetCanceled(token); }
                catch (Exception e)
                {
                    build.Done.TrySetException(e);
                    _ = build.Done.Task.Exception; // observed: every waiter may have gone
                }
                finally
                {
                    if (slot) Slots.Release();
                    // M1: out of Running first, so a release that throws cannot keep the read (and its text) there.
                    Running.TryRemove(new KeyValuePair<string, Build>(key, build));
                    try { release?.Invoke(); } catch (Exception e) when (e is not OutOfMemoryException) { /* per-thread state only */ }
                    build.Cts.Dispose();
                }
            });
        }
        // The request's own token (its deadline, a newer request) ends the wait, never the read.
        return (T)build.Done.Task.WaitAsync(Tier2Cancellation.Current).GetAwaiter().GetResult()!;
    }

    static bool IsCurrent(string key, Build build) => Running.TryGetValue(key, out var current) && ReferenceEquals(current, build);

    static void Cancel(Build b)
    {
        try { b.Cts.Cancel(); } catch (ObjectDisposedException) { /* it ended meanwhile */ }
    }

    /// <summary>M12, lang.forget: ends every read for the document (keys end in "|docId").</summary>
    internal static void Forget(string docId)
    {
        var suffix = "|" + docId;
        foreach (var key in Running.Keys)
            if (key.EndsWith(suffix, StringComparison.Ordinal) && Running.TryRemove(key, out var b)) Cancel(b);
    }
}
