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

    /// <summary>
    /// <c>UPDATE a JOIN b … SET a.x = …, b.y = …</c> sets columns of several tables (MySQL / MariaDB), so a multi-table
    /// UPDATE's SET list offers every table's columns, qualified. Elsewhere a SET target is a column of the one target.
    /// </summary>
    public bool MultiTableUpdate { get; init; }

    /// <summary>
    /// A derived table of a FROM list sees the items before it without LATERAL (DuckDB: <c>FROM t, (SELECT t.x) s</c> and
    /// <c>JOIN (SELECT t.x …) d</c> both run). Elsewhere it sees only the enclosing query levels, never its siblings.
    /// </summary>
    public bool ImplicitLateral { get; init; }

    /// <summary>
    /// Words that may stand between INSERT / UPDATE and the target, or before INTO (MySQL / MariaDB: IGNORE, LOW_PRIORITY,
    /// DELAYED, HIGH_PRIORITY); with <see cref="OrConflictClause"/>, also SQLite's <c>OR ROLLBACK | ABORT | REPLACE | FAIL |
    /// IGNORE</c>. Empty elsewhere: a PostgreSQL table may be called <c>ignore</c>.
    /// </summary>
    public IReadOnlySet<string> DmlModifiers { get; init; } = EmptyWords;

    /// <summary>SQLite's <c>INSERT OR …</c> / <c>UPDATE OR …</c> conflict clause.</summary>
    public bool OrConflictClause { get; init; }

    /// <summary>
    /// A word that ends a FROM item instead of naming its alias, besides the words that do so in every dialect
    /// (<see cref="PgScopeBuilder.NotAlias"/>); <c>next</c> is the token after it, null at the end. The dialect's reserved
    /// words, which no bare alias can be (Oracle START / CONNECT / MINUS / OF, MySQL USE / FORCE / PARTITION / LOCK, DuckDB
    /// QUALIFY / ASOF / SEMI), and clause words that are names elsewhere (Oracle <c>SAMPLE (</c>, DuckDB <c>USING SAMPLE
    /// 10%</c>, SQLite INDEXED). None on PostgreSQL, where <c>FROM t start</c> names the alias start. One PostgreSQL list
    /// used to serve every dialect, so <c>FROM orders START WITH …</c> made START the alias and Enter inserted
    /// START.STATUS (completion audit 2026-11-25, H10).
    /// </summary>
    public Func<string, string?, bool> EndsFromItem { get; init; } = static (_, _) => false;

    /// <summary>Words that join two query operands, each a query level of its own: UNION / INTERSECT / EXCEPT, and Oracle's MINUS.</summary>
    public IReadOnlySet<string> SetOperators { get; init; } = StandardSetOperators;

    static readonly IReadOnlySet<string> EmptyWords = new HashSet<string>();

    /// <summary>UNION / INTERSECT / EXCEPT (declared before <see cref="Postgres"/>, whose initializer reads it).</summary>
    public static readonly IReadOnlySet<string> StandardSetOperators = new HashSet<string>(["UNION", "INTERSECT", "EXCEPT"], StringComparer.OrdinalIgnoreCase);

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
    /// <remarks>
    /// Compared by <see cref="Names"/>: DuckDB's rules are cached case-insensitively by schema, so after a tab's
    /// <c>USE MAIN</c> every tab's rules had DefaultSchema "MAIN", and an ordinal test qualified and demoted every table of
    /// "main" (code audit 2026-11-25, M12).
    /// </remarks>
    public bool Unqualified(string schema) => Names.Equals(schema, DefaultSchema) || SearchPath.Contains(schema, Names);

    /// <summary>
    /// What the document cache keys a document by: the tokenization, not the schema (code audit 2026-11-25, L7). Rules that
    /// differ only in <see cref="DefaultSchema"/> share it, so a script switching schemas (USE, ALTER SESSION) is tokenized
    /// and held once. Must change whenever <see cref="Tokenize"/>, <see cref="Statements"/>, <see cref="Fold"/> or
    /// <see cref="Names"/> do; defaults to <see cref="Id"/>.
    /// </summary>
    public string? DocumentKey { get; init; }

    /// <summary>
    /// Lets go of what <see cref="Tokenize"/> / <see cref="Statements"/> keep per thread (Oracle's copy of the last text),
    /// called on a pool thread that built a document in the background once it is done.
    /// </summary>
    public Action? ReleaseThread { get; init; }

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

public static class RulesCache
{
    /// <summary>How many rules (and the snapshot indexes they hold) a dialect keeps: the schemas a session's scripts switch between.</summary>
    public const int DefaultCapacity = 16;
}

/// <summary>
/// The rules of one dialect per key — the schema or database a script switches to (USE, ALTER SESSION SET CURRENT_SCHEMA)
/// or the login schema the webview sends — at most <paramref name="capacity"/>, least recently used out first. Each
/// instance indexes the snapshot it is used with (<see cref="PgCompletionRules.Schemas"/>), and the key is read while it
/// is typed: before the bound, `USE s`, `USE sa`, `USE sal` … each kept one more index for the life of the sidecar
/// (completion audit 2026-11-25, M13). The key is untrusted text.
/// </summary>
public sealed class RulesCache<TKey>(Func<TKey, PgCompletionRules> create, IEqualityComparer<TKey>? comparer = null, int capacity = RulesCache.DefaultCapacity)
    where TKey : notnull
{
    readonly Dictionary<TKey, LinkedListNode<(TKey Key, PgCompletionRules Rules)>> _map = new(comparer);
    readonly LinkedList<(TKey Key, PgCompletionRules Rules)> _order = new();
    readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    public PgCompletionRules Get(TKey key)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Rules;
            }
            var rules = create(key); // an object initializer: no I/O, nothing that waits
            _map[key] = _order.AddFirst((key, rules));
            while (_map.Count > Math.Max(1, capacity))
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
            return rules;
        }
    }
}
