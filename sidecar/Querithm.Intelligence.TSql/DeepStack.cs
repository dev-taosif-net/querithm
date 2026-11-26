using System.Runtime.ExceptionServices;

namespace Querithm.Intelligence.TSql;

/// <summary>
/// ScriptDom parses, and walks its trees, recursively. A long chain (OR / AND / + / UNION ALL) is a left-deep tree and
/// nested parentheses, CASEs or IFs nest frames, so ordinary SQL overflows a thread-pool thread's stack — and a stack
/// overflow ends the process (the sidecar and every open session); no catch stops it. Measured 2026-11-25, ScriptDom
/// 180.140.0, Windows: on a thread-pool thread a chain of 8,000 terms parses and 16,000 crashes (a 16,000-row
/// <c>UNION ALL</c> load script is about 430 KB); 500 nested parentheses or CASEs parse and 1,000 crash; 1,000 nested IFs
/// parse and 4,000 crash. On a 256 MiB stack chains of 1,000,000 terms (12–24 MB of text), 250,000 nested IFs, BEGINs or
/// CASEs and 64,000 nested parentheses parse; <see cref="TSqlParsing.ParseText"/> refuses deeper text before parsing.
/// Every entry point that parses or walks a tree runs its work here. The stack is reserved, not committed: memory is
/// used only as deep as the work goes. Starting the thread costs about 80 µs (median; p95 240 µs).
/// A new thread per call, not a pool (review 2026-11-25, measured): a run costs 0.090 ms median (p95 0.198 ms) against
/// 0.246 ms for a whole small completion, and a new TSql160Parser parses as fast as a reused one (0.108 ms both). A
/// pooled thread would keep every page of stack a deep parse ever committed for as long as it lives; an exited one
/// returns it.
/// </summary>
public static class DeepStack
{
    public const int StackBytes = 256 * 1024 * 1024;

    [ThreadStatic] static bool _deep;

    /// <summary>
    /// Runs <paramref name="work"/> on a thread with <see cref="StackBytes"/> of stack (inline when already on one). When
    /// the OS refuses the thread (a strict overcommit limit on Linux counts the reserved stack), the call fails with an
    /// <see cref="InvalidOperationException"/> callers report like any failure — never inline on a small stack, which a
    /// deep text could overflow, ending the process.
    /// </summary>
    public static T Run<T>(Func<T> work) => Run(work, static t => t.Start());

    /// <summary><see cref="Run{T}(Func{T})"/> with the thread started by <paramref name="start"/> (tests fail it as the OS can).</summary>
    internal static T Run<T>(Func<T> work, Action<Thread> start)
    {
        if (_deep) return work();
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            _deep = true;
            try { result = work(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        }, StackBytes) { IsBackground = true, Name = "querithm-tsql" };
        try { start(thread); }
        catch (OutOfMemoryException e)
        {
            throw new InvalidOperationException("The SQL Server analysis could not start: the system refused the memory for its thread. Close other programs and try again.", e);
        }
        thread.Join(); // CPU work in this process; its own token (completion, references) ends it early
        failure?.Throw();
        return result;
    }

    public static void Run(Action work) => Run<object?>(() => { work(); return null; });

    /// <summary>Whether the calling thread is a deep-stack thread (tests).</summary>
    internal static bool OnDeepStack => _deep;
}
