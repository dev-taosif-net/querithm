namespace Querithm.Contract;

/// <summary>
/// A reusable piece of SQL inserted by its prefix (Monaco snippet syntax, <c>${1:name}</c>). User snippets live in
/// store.db (user data, never logged); built-in ones ship in code, read-only, with ids starting <c>builtin:</c>.
/// </summary>
public sealed record Snippet
{
    /// <summary>ULID; empty on a new snippet (the sidecar assigns it).</summary>
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>What completion matches: letters, digits and '_' only, 1–40 characters, unique (ignoring case).</summary>
    public required string Prefix { get; init; }
    public required string Body { get; init; }
    /// <summary>Only offered in tabs of this dialect; null = every dialect.</summary>
    public Dialect? Dialect { get; init; }
    public string[] Tags { get; init; } = [];
    public long UpdatedAt { get; init; }
    public bool BuiltIn { get; init; }
}

public sealed record SnippetSaveParams
{
    public required Snippet Snippet { get; init; }
}

public sealed record SnippetRef
{
    public required string Id { get; init; }
}

/// <summary>Limits checked by the sidecar on every save (§13: untrusted input is validated at the RPC boundary).</summary>
public static class SnippetLimits
{
    public const int Title = 200;
    public const int Prefix = 40;
    public const int BodyBytes = 256 * 1024;
    public const int Tags = 20;
    public const int Tag = 40;
    public const int Count = 5_000;
}
