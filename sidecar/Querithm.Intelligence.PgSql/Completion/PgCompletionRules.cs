using System.Runtime.CompilerServices;
using Querithm.Contract;

namespace Querithm.Intelligence.PgSql.Completion;

/// <summary>
/// The dialect facts tier-2 completion depends on (Phase D3): PostgreSQL's by default, and DuckDB's — a PG-like grammar
/// whose names keep their case but compare case-insensitively, whose dollar-quoted text is a plain string, and whose
/// statements end only at ';'. SQLite and MySQL / MariaDB (Phase D1 / D2) bring their own tokens, quotes and statement
/// boundaries. Completion runs synchronously, so the rules of one call are scoped to its thread
/// (<see cref="Use"/>); everything else in the engine is unchanged.
/// </summary>
public sealed class PgCompletionRules
{
    /// <summary>Distinguishes the document cache of each dialect (a docId can move between connections).</summary>
    public required string Id { get; init; }
    /// <summary>How names compare once read from the text: exact after folding (PostgreSQL), or case-insensitively.</summary>
    public required StringComparer Names { get; init; }
    /// <summary>Unquoted identifiers fold to lower case (PostgreSQL); otherwise they keep the case they were typed in.</summary>
    public required bool FoldsUnquoted { get; init; }
    /// <summary>The schema unqualified names resolve to first, left out of labels.</summary>
    public required string DefaultSchema { get; init; }
    public required Func<string, bool> IsReserved { get; init; }
    /// <summary>A name that needs no quotes in this dialect.</summary>
    public required Func<string, bool> IsPlain { get; init; }
    /// <summary>Dollar-quoted text after AS / DO holds code (PL/pgSQL bodies); otherwise it is a string literal.</summary>
    public required bool DollarBodiesAreCode { get; init; }
    public required Func<string, List<PgToken>> Tokenize { get; init; }
    /// <summary>The statements of a document: its text and <see cref="Tokenize"/>'s tokens of it (MySQL's delimiter needs the text).</summary>
    public required Func<string, List<PgToken>, List<List<PgToken>>> Statements { get; init; }
    public required IReadOnlyList<string> BuiltinTypes { get; init; }
    /// <summary>The name a quoted identifier token stands for (PostgreSQL: "x" with "" doubled).</summary>
    public Func<string, string> Unquote { get; init; } = PgIdent.UnquoteDoubleQuoted;
    /// <summary>Quotes a name that is not plain.</summary>
    public Func<string, string> Delimit { get; init; } = s => "\"" + s.Replace("\"", "\"\"") + "\"";
    /// <summary>Calls take <c>name =&gt; value</c> arguments, so completion offers parameter names.</summary>
    public bool NamedArguments { get; init; } = true;
    /// <summary>
    /// Built-in functions signature help knows besides the catalog's routines (schema "", <see cref="SnapObject.ReturnType"/>
    /// holds the description, parameter names are shown as written): for an engine that stores no routines (SQLite).
    /// </summary>
    public IReadOnlyList<SnapObject> BuiltinRoutines { get; init; } = [];

    /// <summary>The spelling an unquoted name folds to when <see cref="FoldsUnquoted"/> (PostgreSQL: lower case; Oracle: upper case).</summary>
    public Func<string, string> Fold { get; init; } = s => s.ToLowerInvariant();

    /// <summary>
    /// Schemas whose objects an unqualified name also reaches after <see cref="DefaultSchema"/>, in order, and which are
    /// never written as a qualifier (Oracle: PUBLIC, the public synonyms). Their objects are labelled without a schema.
    /// </summary>
    public IReadOnlyList<string> SearchPath { get; init; } = [];

    /// <summary>An unterminated string literal by the dialect's own quoting (Oracle's q'[…]'), when the default check cannot tell.</summary>
    public Func<PgToken, bool>? StringOpen { get; init; }

    /// <summary>
    /// Routines a dotted call names that the snapshot holds only as display text (Oracle package subprograms): the folded
    /// name chain before the parenthesis (pkg.member, schema.pkg.member) → routines for signature help and named arguments.
    /// </summary>
    public Func<SchemaSnapshot, IReadOnlyList<string>, IReadOnlyList<SnapObject>>? MemberRoutines { get; init; }

    /// <summary>How signature help names a routine (default: schema.name, each part quoted when it needs it).</summary>
    public Func<SnapObject, string>? RoutineLabel { get; init; }

    /// <summary>A schema whose objects are written without a qualifier: the default one and <see cref="SearchPath"/>.</summary>
    public bool Unqualified(string schema) => schema == DefaultSchema || SearchPath.Contains(schema);

    internal readonly ConditionalWeakTable<SchemaSnapshot, PgSchema> Schemas = new();

    public string Quote(string name) => IsPlain(name) ? name : Delimit(name);

    public static readonly PgCompletionRules Postgres = new()
    {
        Id = "pg", Names = StringComparer.Ordinal, FoldsUnquoted = true, DefaultSchema = "public",
        IsReserved = w => PgSplitter.Reserved.Contains(w),
        IsPlain = s => s.Length > 0 && (char.IsAsciiLetterLower(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '$')
                       && !PgSplitter.Reserved.Contains(s),
        DollarBodiesAreCode = true, Tokenize = PgTokenizer.Tokenize, Statements = (_, tokens) => PgTokenizer.Statements(tokens),
        BuiltinTypes =
        [
            "integer", "bigint", "smallint", "numeric", "real", "double precision", "text", "varchar", "char", "boolean", "date", "time",
            "timestamp", "timestamptz", "interval", "uuid", "json", "jsonb", "bytea", "record", "inet", "money", "oid", "regclass", "xml",
        ],
    };

    [ThreadStatic] static PgCompletionRules? _current;

    /// <summary>The rules of the completion call running on this thread (PostgreSQL's outside one).</summary>
    internal static PgCompletionRules Current => _current ?? Postgres;

    /// <summary>Scopes <paramref name="rules"/> to the calls made before the returned value is disposed (same thread).</summary>
    public static IDisposable Use(PgCompletionRules rules)
    {
        var previous = _current;
        _current = rules;
        return new Restore(previous);
    }

    sealed class Restore(PgCompletionRules? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }
}
