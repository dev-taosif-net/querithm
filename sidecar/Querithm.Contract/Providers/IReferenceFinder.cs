namespace Querithm.Contract.Providers;

// Find usages and safe rename: references to one schema object or column in a piece of SQL, found on the
// AST (T-SQL) or tokens (PostgreSQL), never by searching text.

/// <summary>What the cursor is on, resolved against the snapshot (a column: <see cref="Schema"/>.<see cref="Name"/> is its table).</summary>
public sealed record RefTarget(RefKind Kind, string Schema, string Name, string? Column = null);

public enum RefKind { Table, View, Procedure, Function, Column, Type, Sequence }

public enum RefConfidence { Exact, Possible }

/// <summary>One reference: the identifier's span in the text (quotes included), and why it is only possible.</summary>
public sealed record RefHit(int Offset, int Length, RefConfidence Confidence, string? Reason = null, bool Definition = false);

public sealed record RefScan(IReadOnlyList<RefHit> Hits, bool DynamicSql = false, bool ParseFailed = false);

/// <summary>What the finder may ask about the database: an object's columns, and the schemas holding a name.</summary>
public sealed class RefContext
{
    /// <summary>The schema an unqualified name resolves to first (dbo, public, or the module's own).</summary>
    public required string DefaultSchema { get; init; }
    /// <summary>Columns of (schema, table), or null when the table is unknown (temp table, CTE, another database).</summary>
    public required Func<string, string, IReadOnlyCollection<string>?> Columns { get; init; }
    /// <summary>The schemas that have an object with this name (for unqualified references).</summary>
    public required Func<string, IReadOnlyCollection<string>> SchemasOf { get; init; }
}

public static class RefText
{
    static bool Part(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '@' or '#';

    /// <summary>
    /// Whether <paramref name="name"/> occurs as a whole word (case-insensitive) in the range: a cheap pre-filter before
    /// a parse. It may pass on strings and comments (the parse decides); it never fails on a real reference.
    /// </summary>
    public static bool ContainsWord(string text, string name, int start, int length)
    {
        var end = start + length;
        for (var i = text.IndexOf(name, start, length, StringComparison.OrdinalIgnoreCase); i >= 0 && i + name.Length <= end;
             i = i + 1 < end ? text.IndexOf(name, i + 1, end - i - 1, StringComparison.OrdinalIgnoreCase) : -1)
        {
            var before = i == 0 ? ' ' : text[i - 1];
            var after = i + name.Length >= text.Length ? ' ' : text[i + name.Length];
            if (!Part(before) && !Part(after)) return true;
        }
        return false;
    }

    public static bool ContainsWord(string text, string name) => ContainsWord(text, name, 0, text.Length);
}

public interface IReferenceFinder
{
    /// <summary>Every reference to <paramref name="target"/> in <paramref name="text"/> (a module definition or a tab).</summary>
    RefScan Find(string text, RefTarget target, RefContext ctx);

    /// <summary>The schema object or column at <paramref name="offset"/>, or null when it is not one (keyword, local symbol, literal).</summary>
    RefTarget? TargetAt(string text, int offset, RefContext ctx);

    /// <summary>Spans of the local symbol at <paramref name="offset"/> (variable, parameter, CTE, alias) in its scope, or null.</summary>
    IReadOnlyList<(int Offset, int Length)>? LocalOccurrences(string text, int offset);

    /// <summary>The new name written the way <paramref name="original"/> was (bracketed, quoted or bare when it can be).</summary>
    string Rename(string original, string newName);

    /// <summary>The definition is stored parsed by the server (PostgreSQL views, BEGIN ATOMIC functions): it follows a rename by itself.</summary>
    bool ServerTracked(string definition, ObjectKind kind);

    /// <summary>
    /// Phase D4 S11: the rename dialog's new name in the catalog's spelling, as the engine reads it typed (Oracle folds
    /// <c>emp2</c> to EMP2 and keeps <c>"emp2"</c> exact). Default: as typed.
    /// </summary>
    string CatalogName(string typed) => typed;
}
