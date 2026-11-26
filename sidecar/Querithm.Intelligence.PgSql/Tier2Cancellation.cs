using System.Runtime.CompilerServices;

namespace Querithm.Intelligence.PgSql;

/// <summary>
/// The token of the tier-2 request running on this thread, for the tokenizers and statement splitters the PostgreSQL
/// family shares between completion and the guard (their signatures take no token, and the guard must not stop). Inside a
/// <see cref="Use"/> scope they throw <see cref="OperationCanceledException"/> within 1,024 tokens of the cancel; outside
/// one (classification, folding) the token is <c>default</c> and never fires. Before 2026-11-25 a request was checked
/// only on entry and in nested column resolution, so a superseded request on a 1 MB document ran 0.7–1.7 s to its end
/// (<c>Tier2CancellationTests</c>). Completion is synchronous on one thread, so the scope is per thread (as
/// <see cref="Completion.PgCompletionRules.Use"/>).
/// </summary>
public static class Tier2Cancellation
{
    [ThreadStatic] static CancellationToken _current;

    /// <summary>The running request's token (<c>default</c> outside a request).</summary>
    public static CancellationToken Current => _current;

    /// <summary>Scopes <paramref name="ct"/> to the calls made on this thread before the returned value is disposed.</summary>
    public static Scope Use(CancellationToken ct)
    {
        var previous = _current;
        _current = ct;
        return new Scope(previous);
    }

    public readonly struct Scope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }

    /// <summary>On every 1,024th <paramref name="i"/>: throws when the running request was cancelled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check(int i)
    {
        if ((i & 1023) == 0) _current.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// <paramref name="source"/> mapped one to one (another dialect's tokens to <see cref="PgToken"/>s), checking the
    /// running request on the first item and every 1,024th. Review 2026-11-25: MySQL, Oracle and SQLite mapped every
    /// token of a 1 MB document (and every statement's) with an unchecked Select, after the checked lexing: a cancel that
    /// landed there let the request run on 92–208 ms and finish (<c>Tier2CancellationTests</c>, the integration run).
    /// </summary>
    public static List<TOut> Map<TIn, TOut>(IReadOnlyList<TIn> source, Func<TIn, TOut> map)
    {
        var list = new List<TOut>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            Check(i);
            list.Add(map(source[i]));
        }
        return list;
    }
}
