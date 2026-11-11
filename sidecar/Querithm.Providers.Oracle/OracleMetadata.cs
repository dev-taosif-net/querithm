using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>
/// The Oracle catalog (D4 S05, ARCHITECTURE §8). Namespaces are the schemas the login can see objects in that Oracle does
/// not maintain (<c>ALL_USERS.ORACLE_MAINTAINED = 'N'</c>), plus the current schema, all under the connection's one service
/// (§16 "Oracle namespaces"). Everything is read from <c>ALL_*</c> views, never <c>DBA_*</c>.
/// <para>
/// One dictionary query per refresh (<see cref="StampSql"/>) lists every table, view, materialized view (a view with a note),
/// procedure, function, trigger (parent: its table or view), type, sequence, synonym (private, and the public ones that
/// point into a listed schema), package specification and package body (D4 S08: two objects, the body naming its
/// specification — <see cref="Assemble"/>), each with a marker the server computes with <c>STANDARD_HASH</c> over its columns,
/// indexes, constraints, triggers, PL/SQL source or attributes — <c>LAST_DDL_TIME</c> alone is not trusted (GRANT moves it,
/// and it says nothing about what changed). Each dictionary view appears once in that query: Oracle's hard parse of a
/// query over the <c>ALL_*</c> views grows with every reference (measured, §14 metric 10).
/// </para>
/// <para>
/// A view the login cannot read (ORA-00942, ORA-01031: a hardened database that revoked it from PUBLIC) is left out of the
/// query and said in <see cref="Notes"/> — what is missing and why — instead of failing the refresh. Object ids are
/// <c>ALL_OBJECTS.OBJECT_ID</c>; names are kept exactly (<c>"Sales"</c> and <c>SALES</c> are two tables).
/// Definitions come from <c>DBMS_METADATA.GET_DDL</c> on demand, with <c>ALL_SOURCE</c> / <c>ALL_VIEWS</c> when the login may
/// not call it for the object. Row counts are <c>ALL_TABLES.NUM_ROWS</c> (statistics), never <c>COUNT(*)</c>.
/// </para>
/// </summary>
public sealed class OracleMetadata : IMetadataProvider
{
    /// <summary>ORA-00942 table or view does not exist, ORA-01031 insufficient privileges.</summary>
    internal static bool IsDenied(Exception ex) => ex is OracleException { Number: 942 or 1031 };

    /// <summary>The optional dictionary views and what is lost without each. ALL_OBJECTS and ALL_USERS are the catalog itself: without them the refresh fails.</summary>
    internal enum Piece { Tables, ExternalTables, MViews, Views, Columns, Indexes, IndexColumns, Constraints, ConstraintColumns, Triggers, Source, Sequences, Synonyms, Arguments, Procedures }

    internal static readonly IReadOnlyDictionary<Piece, (string View, string Effect)> Pieces = new Dictionary<Piece, (string, string)>
    {
        [Piece.Tables] = ("ALL_TABLES", "Tables are not listed"),
        [Piece.ExternalTables] = ("ALL_EXTERNAL_TABLES", "External tables are not marked as such"),
        [Piece.MViews] = ("ALL_MVIEWS", "Changes to materialized views are detected only by their DDL time"),
        [Piece.Views] = ("ALL_VIEWS", "Changes to views are detected only by their DDL time"),
        [Piece.Columns] = ("ALL_TAB_COLS", "Columns are not shown, and column changes are not detected"),
        [Piece.Indexes] = ("ALL_INDEXES", "Indexes are not shown, and index changes are not detected"),
        [Piece.IndexColumns] = ("ALL_IND_COLUMNS", "Index columns are not shown"),
        [Piece.Constraints] = ("ALL_CONSTRAINTS", "Keys and constraints are not shown, and their changes are not detected"),
        [Piece.ConstraintColumns] = ("ALL_CONS_COLUMNS", "Key columns are not shown"),
        [Piece.Triggers] = ("ALL_TRIGGERS", "Triggers are not listed"),
        [Piece.Source] = ("ALL_SOURCE", "Changes to procedures, functions, triggers, types and packages are detected only by their DDL time"),
        [Piece.Sequences] = ("ALL_SEQUENCES", "Changes to sequences are detected only by their DDL time"),
        [Piece.Synonyms] = ("ALL_SYNONYMS", "Public synonyms are not listed"),
        [Piece.Arguments] = ("ALL_ARGUMENTS", "Parameters and return types are not shown"),
        [Piece.Procedures] = ("ALL_PROCEDURES", "Package members are not shown"),
    };

    /// <summary>At most this many notes per refresh, each bounded: they are shown, stored and sent as they are.</summary>
    const int MaxNotes = 20;

    static readonly ConditionalWeakTable<DbConnection, List<string>> NotesByConnection = new();

    readonly Func<string, string> _view;

    public OracleMetadata() : this(null) { }

    /// <param name="view">Tests only: renames a dictionary view (a name the server does not know gives a real ORA-00942).</param>
    internal OracleMetadata(Func<string, string>? view) => _view = view ?? (v => v);

    public IReadOnlyList<string> Notes(DbConnection connection) =>
        NotesByConnection.TryGetValue(connection, out var list) ? [.. list] : [];

    static void Note(DbConnection connection, string note)
    {
        var list = NotesByConnection.GetOrCreateValue(connection);
        lock (list)
            if (list.Count < MaxNotes && !list.Contains(note, StringComparer.Ordinal)) list.Add(note.Length > 300 ? note[..300] : note);
    }

    internal static string DeniedNote(string effect, string view, Exception ex) =>
        string.Create(CultureInfo.InvariantCulture, $"{effect}: this login cannot read {view} (ORA-{((OracleException)ex).Number:00000}).");

    // ------------------------------------------------------------------------------------------ the stamp query

    /// <summary>A 60-bit number from SHA-256 over the text: sums of these stay exact in NUMBER and fit a long.</summary>
    static string H(string text) => $"TO_NUMBER(SUBSTR(RAWTOHEX(STANDARD_HASH(NVL({text}, '~'), 'SHA256')), 1, 15), 'XXXXXXXXXXXXXXX')";
    static string J(params string[] parts) => string.Join(" || '|' || ", parts);
    const string InS = "IN (SELECT owner FROM s)";
    const string DdlTime = "TO_CHAR(o.last_ddl_time, 'YYYYMMDDHH24MISS')";

    /// <summary>
    /// Statistics feedback off for Querithm's own dictionary queries. Without it the server re-optimizes the stamp query after
    /// each of its first runs, a hard parse each time (measured on the fixture, 997 objects: 2.2, 4.3, 4.0, 4.0, 2.0 then 0.8 s;
    /// with it 2.3 s then 0.7–1.3 s). OPT_PARAM is a documented hint; the parameter is an internal one, and a hint the server
    /// does not know is ignored, so a release without it only brings back the re-parses (ARCHITECTURE §16).
    /// </summary>
    internal const string NoFeedback = "/*+ OPT_PARAM('_optimizer_use_feedback' 'false') */";

    /// <summary>
    /// The one marker query (§8): a row per object — id, owner, name, ALL_OBJECTS type, marker, return type, parent id, status,
    /// flag (X external table; U a package specification the login's own user owns, whose body it always sees). Each view in <paramref name="missing"/> is left out, with what depends on it.
    /// </summary>
    internal string StampSql(IReadOnlySet<Piece> missing)
    {
        bool Has(Piece p) => !missing.Contains(p);
        string V(string name) => _view(name);
        string Or(Piece p, string expr) => Has(p) ? expr : "NULL";
        var joins = new StringBuilder();
        void Join(Piece p, string sql) { if (Has(p)) joins.Append('\n').Append(sql); }

        Join(Piece.Tables, $"LEFT JOIN {V("ALL_TABLES")} t ON o.object_type = 'TABLE' AND t.owner = o.owner AND t.table_name = o.object_name");
        Join(Piece.ExternalTables, $"LEFT JOIN {V("ALL_EXTERNAL_TABLES")} x ON o.object_type = 'TABLE' AND x.owner = o.owner AND x.table_name = o.object_name");
        Join(Piece.MViews, $"LEFT JOIN {V("ALL_MVIEWS")} mv ON o.object_type = 'MATERIALIZED VIEW' AND mv.owner = o.owner AND mv.mview_name = o.object_name");
        Join(Piece.Views, $"LEFT JOIN {V("ALL_VIEWS")} v ON o.object_type = 'VIEW' AND v.owner = o.owner AND v.view_name = o.object_name");
        Join(Piece.Columns, $"""
            LEFT JOIN (SELECT owner, table_name, SUM({H(J("internal_column_id", "column_name", "data_type", "data_type_owner", "data_length", "char_length", "char_used",
                "data_precision", "data_scale", "nullable", "hidden_column", "virtual_column", "identity_column", "user_generated", "default_on_null", H("data_default_vc")))}) AS h
                FROM {V("ALL_TAB_COLS")} WHERE owner {InS} GROUP BY owner, table_name) c
              ON o.object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW') AND c.owner = o.owner AND c.table_name = o.object_name
            """);
        Join(Piece.Indexes, $"""
            LEFT JOIN (SELECT table_owner, table_name, SUM({H(J("owner", "index_name", "uniqueness", "index_type", "status", "funcidx_status", "visibility"))}) AS h
                FROM {V("ALL_INDEXES")} WHERE table_owner {InS} GROUP BY table_owner, table_name) i
              ON o.object_type = 'TABLE' AND i.table_owner = o.owner AND i.table_name = o.object_name
            """);
        Join(Piece.IndexColumns, $"""
            LEFT JOIN (SELECT table_owner, table_name, SUM({H(J("index_owner", "index_name", "column_position", "column_name", "descend"))}) AS h
                FROM {V("ALL_IND_COLUMNS")} WHERE table_owner {InS} GROUP BY table_owner, table_name) ic
              ON o.object_type = 'TABLE' AND ic.table_owner = o.owner AND ic.table_name = o.object_name
            """);
        Join(Piece.Constraints, $"""
            LEFT JOIN (SELECT owner, table_name, SUM({H(J("constraint_name", "constraint_type", "status", "deferrable", "deferred", "validated", "r_owner", "r_constraint_name",
                "delete_rule", H("search_condition_vc")))}) AS h
                FROM {V("ALL_CONSTRAINTS")} WHERE owner {InS} GROUP BY owner, table_name) k
              ON o.object_type = 'TABLE' AND k.owner = o.owner AND k.table_name = o.object_name
            """);
        Join(Piece.ConstraintColumns, $"""
            LEFT JOIN (SELECT owner, table_name, SUM({H(J("constraint_name", "position", "column_name"))}) AS h
                FROM {V("ALL_CONS_COLUMNS")} WHERE owner {InS} GROUP BY owner, table_name) kc
              ON o.object_type = 'TABLE' AND kc.owner = o.owner AND kc.table_name = o.object_name
            """);
        if (Has(Piece.Triggers))
            joins.Append('\n').Append($"""
                LEFT JOIN (SELECT table_owner, table_name, SUM({H(J("owner", "trigger_name", "status"))}) AS h
                    FROM tr WHERE base_object_type IN ('TABLE', 'VIEW') GROUP BY table_owner, table_name) g
                  ON o.object_type = 'TABLE' AND g.table_owner = o.owner AND g.table_name = o.object_name
                LEFT JOIN tr ON o.object_type = 'TRIGGER' AND tr.owner = o.owner AND tr.trigger_name = o.object_name
                LEFT JOIN o p ON p.owner = tr.table_owner AND p.object_name = tr.table_name AND p.object_type = tr.base_object_type
                """);
        Join(Piece.Source, $"""
            LEFT JOIN (SELECT owner, name, CASE type WHEN 'TYPE BODY' THEN 'TYPE' ELSE type END AS type, SUM({H(J("type", "line", H("text")))}) AS h
                FROM {V("ALL_SOURCE")} WHERE owner {InS} AND type IN ('PROCEDURE', 'FUNCTION', 'TRIGGER', 'TYPE', 'TYPE BODY', 'PACKAGE', 'PACKAGE BODY')
                GROUP BY owner, name, CASE type WHEN 'TYPE BODY' THEN 'TYPE' ELSE type END) src
              ON src.owner = o.owner AND src.name = o.object_name AND src.type = o.object_type
            """);
        Join(Piece.Sequences, $"LEFT JOIN {V("ALL_SEQUENCES")} q ON o.object_type = 'SEQUENCE' AND q.sequence_owner = o.owner AND q.sequence_name = o.object_name");
        Join(Piece.Synonyms, "LEFT JOIN y ON o.object_type = 'SYNONYM' AND y.owner = o.owner AND y.synonym_name = o.object_name");
        Join(Piece.Arguments, $"""
            LEFT JOIN {V("ALL_ARGUMENTS")} a ON o.object_type = 'FUNCTION' AND a.object_id = o.object_id AND a.package_name IS NULL AND a.position = 0 AND a.data_level = 0
            """);

        // Without a view, what it would have hashed is replaced by the object's DDL time, so a change is still seen.
        var weak = missing.Count > 0 ? DdlTime : "NULL";
        var marker = H(J(
            "o.status", Or(Piece.Columns, "c.h"), Or(Piece.Indexes, "i.h"), Or(Piece.IndexColumns, "ic.h"), Or(Piece.Constraints, "k.h"),
            Or(Piece.ConstraintColumns, "kc.h"), Or(Piece.Triggers, "g.h"), Or(Piece.Source, "src.h"),
            Or(Piece.ExternalTables, "CASE WHEN x.table_name IS NOT NULL THEN 'X' END"),
            $"CASE WHEN o.object_type IN ('VIEW', 'MATERIALIZED VIEW') THEN {DdlTime} END",
            Or(Piece.Views, "v.text_length"), Or(Piece.Views, H("v.text_vc")),
            Or(Piece.MViews, "mv.query_len"), Or(Piece.MViews, "mv.refresh_mode"), Or(Piece.MViews, "mv.refresh_method"),
            Or(Piece.Triggers, "tr.status"), Or(Piece.Triggers, "tr.table_owner"), Or(Piece.Triggers, "tr.table_name"),
            Or(Piece.Sequences, "q.min_value"), Or(Piece.Sequences, "q.max_value"), Or(Piece.Sequences, "q.increment_by"),
            Or(Piece.Sequences, "q.cycle_flag"), Or(Piece.Sequences, "q.order_flag"), Or(Piece.Sequences, "q.cache_size"),
            Or(Piece.Synonyms, "y.table_owner"), Or(Piece.Synonyms, "y.table_name"), Or(Piece.Synonyms, "y.db_link"), weak));

        var synonyms = Has(Piece.Synonyms)
            ? $"""
              ,
              y AS (SELECT owner, synonym_name, table_owner, table_name, db_link FROM {V("ALL_SYNONYMS")}
                    WHERE owner {InS} OR owner = 'PUBLIC' AND table_owner {InS})
              """
            : "";
        var publicSynonyms = Has(Piece.Synonyms) ? "\n     OR owner = 'PUBLIC' AND object_type = 'SYNONYM' AND (owner, object_name) IN (SELECT owner, synonym_name FROM y)" : "";
        var triggers = Has(Piece.Triggers)
            ? $"""
              ,
              tr AS (SELECT owner, trigger_name, status, table_owner, table_name, base_object_type FROM {V("ALL_TRIGGERS")}
                     WHERE owner {InS} OR table_owner {InS})
              """
            : "";
        // A table is listed when ALL_TABLES says it is a real one (not in the recycle bin, not a domain index's secondary
        // table, an IOT overflow or a nested table's storage) and it is not a materialized view's container table.
        var table = Has(Piece.Tables)
            ? "CASE WHEN t.dropped = 'NO' AND t.secondary = 'N' AND t.nested = 'NO' AND (t.iot_type IS NULL OR t.iot_type = 'IOT')"
              + " AND NOT EXISTS (SELECT 1 FROM o m WHERE m.owner = o.owner AND m.object_name = o.object_name AND m.object_type = 'MATERIALIZED VIEW') THEN 1 END"
            : "NULL";
        var trigger = Has(Piece.Triggers) ? "CASE WHEN p.object_id IS NOT NULL THEN 1 END" : "NULL";
        // D4 S09: the listed object a synonym names (not across a database link), for tier-2 completion. Tables, views,
        // routines, packages, types, sequences and synonyms share one namespace per schema; a materialized view outranks the
        // container table of the same name, which is not listed.
        var target = Has(Piece.Synonyms)
            ? """
              CASE WHEN o.object_type = 'SYNONYM' AND y.db_link IS NULL THEN
                (SELECT MIN(t.object_id) KEEP (DENSE_RANK FIRST ORDER BY CASE t.object_type WHEN 'MATERIALIZED VIEW' THEN 0 ELSE 1 END) FROM o t
                 WHERE t.owner = y.table_owner AND t.object_name = y.table_name AND t.object_type NOT IN ('PACKAGE BODY', 'TRIGGER')) END
              """
            : "NULL";
        var synonym = Has(Piece.Synonyms) ? "CASE WHEN o.owner <> 'PUBLIC' OR y.synonym_name IS NOT NULL THEN 1 END" : "CASE WHEN o.owner <> 'PUBLIC' THEN 1 END";

        return $"""
            WITH s AS (
              SELECT u.username AS owner FROM {V("ALL_USERS")} u
              WHERE u.username = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')
                 OR u.oracle_maintained = 'N' AND EXISTS (SELECT 1 FROM {V("ALL_OBJECTS")} x WHERE x.owner = u.username)){synonyms},
            o AS (SELECT object_id, owner, object_name, object_type, status, generated, last_ddl_time FROM {V("ALL_OBJECTS")}
                  WHERE subobject_name IS NULL AND (owner {InS}
                    AND object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW', 'PROCEDURE', 'FUNCTION', 'TRIGGER', 'TYPE', 'SEQUENCE', 'SYNONYM', 'PACKAGE', 'PACKAGE BODY'){publicSynonyms})){triggers}
            SELECT {NoFeedback} o.object_id, o.owner, o.object_name, o.object_type, {marker} AS marker,
              {Or(Piece.Arguments, "CASE WHEN a.type_name IS NOT NULL THEN a.type_owner || '.' || a.type_name ELSE a.data_type END")} AS rtype,
              {Or(Piece.Triggers, "p.object_id")} AS parent, o.status,
              CASE WHEN o.object_type = 'PACKAGE' AND o.owner = SYS_CONTEXT('USERENV', 'SESSION_USER') THEN 'U'
                ELSE {Or(Piece.ExternalTables, "CASE WHEN x.table_name IS NOT NULL THEN 'X' END")} END AS flag,
              {target} AS target
            FROM o{joins}
            WHERE CASE o.object_type
              WHEN 'TABLE' THEN {table}
              WHEN 'TRIGGER' THEN {trigger}
              WHEN 'TYPE' THEN CASE WHEN o.generated = 'N' THEN 1 END
              WHEN 'SEQUENCE' THEN CASE WHEN o.generated = 'N' THEN 1 END
              WHEN 'SYNONYM' THEN {synonym}
              ELSE 1 END = 1
            """;
    }

    /// <summary>One row of <see cref="StampSql"/>, as the unit tests feed it.</summary>
    internal sealed record StampRow(long Id, string Owner, string Name, string Type, long Marker, string? ReturnType, long? Parent, string? Status, string? Flag, long? Target = null);

    internal static ObjectKind? KindOf(string type) => type switch
    {
        "TABLE" => ObjectKind.Table,
        "VIEW" or "MATERIALIZED VIEW" => ObjectKind.View,
        "PROCEDURE" => ObjectKind.Proc,
        "FUNCTION" => ObjectKind.Func,
        "TRIGGER" => ObjectKind.Trigger,
        "TYPE" => ObjectKind.Type,
        "SEQUENCE" => ObjectKind.Sequence,
        "SYNONYM" => ObjectKind.Synonym,
        "PACKAGE" => ObjectKind.Package,
        "PACKAGE BODY" => ObjectKind.PackageBody,
        _ => null,
    };

    /// <summary>A package specification of the login's own user without a body: it has none (the owner always sees its body).</summary>
    internal const string NoBody = "no body";

    /// <summary>
    /// A package specification of another schema without a visible body: ALL_OBJECTS lists the body only to a login with
    /// DEBUG on the package (or CREATE ANY PROCEDURE / DEBUG ANY PROCEDURE); EXECUTE shows the specification alone
    /// (measured on the Free fixture). Whether there is a body at all cannot be told apart.
    /// </summary>
    internal const string BodyNotVisible = "body not visible";

    /// <summary>What the explorer shows beside the name: a materialized view, an external table, an invalid object.</summary>
    internal static string? NoteOf(string type, string? status, string? flag)
    {
        var parts = new List<string>(2);
        if (type == "MATERIALIZED VIEW") parts.Add("materialized view");
        if (flag == "X") parts.Add("external table");
        if (status == "INVALID") parts.Add("invalid");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// The snapshot's stamps from the query's rows (unit tests feed canned rows). A package body names its specification
    /// (same owner and name, exact); a specification without a listed body says why (<see cref="NoBody"/>,
    /// <see cref="BodyNotVisible"/>). Each one's marker also covers the other's id, so a body created, dropped or seen for
    /// the first time (a DEBUG grant) re-reads the specification and its note, which its own source does not change.
    /// </summary>
    internal static List<ObjectStamp> Assemble(IEnumerable<StampRow> rows)
    {
        var all = rows.ToList();
        var specs = new Dictionary<(string, string), long>();
        var bodies = new Dictionary<(string, string), long>();
        foreach (var r in all)
            if (r.Type == "PACKAGE") specs[(r.Owner, r.Name)] = r.Id;
            else if (r.Type == "PACKAGE BODY") bodies[(r.Owner, r.Name)] = r.Id;
        var list = new List<ObjectStamp>();
        foreach (var r in all)
        {
            if (KindOf(r.Type) is not { } kind) continue;
            var note = NoteOf(r.Type, r.Status, r.Flag);
            var marker = r.Marker;
            long? spec = null;
            if (kind == ObjectKind.Package)
            {
                var hasBody = bodies.TryGetValue((r.Owner, r.Name), out var body);
                marker = Mix(marker, hasBody ? body : null);
                if (!hasBody)
                {
                    var why = r.Flag == "U" ? NoBody : BodyNotVisible;
                    note = note == null ? why : $"{note}, {why}";
                }
            }
            else if (kind == ObjectKind.PackageBody)
            {
                spec = specs.TryGetValue((r.Owner, r.Name), out var sid) ? sid : null;
                marker = Mix(marker, spec);
            }
            // A synonym's target id is in its marker: a target listed for the first time (created later, a new grant) re-reads it.
            long? target = kind == ObjectKind.Synonym ? r.Target : null;
            if (kind == ObjectKind.Synonym) marker = Mix(marker, target);
            list.Add(new ObjectStamp(r.Id, r.Owner, r.Name, kind, marker, kind == ObjectKind.Func ? r.ReturnType : null,
                kind == ObjectKind.Trigger ? r.Parent : null, Note: note, SpecObjectId: spec, TargetObjectId: target));
        }
        return list;
    }

    /// <summary>A marker that also changes with <paramref name="other"/>, kept in the 60 bits the query's markers use.</summary>
    static long Mix(long marker, long? other) =>
        marker ^ (unchecked((other ?? -1L) * -7046029254386353131L) & 0x0FFF_FFFF_FFFF_FFFFL);

    static OracleCommand Command(DbConnection connection, string sql)
    {
        var cmd = (OracleCommand)connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        cmd.FetchSize = OracleProvider.FetchSizeBytes;
        cmd.InitialLONGFetchSize = -1;
        return cmd;
    }

    static long Long(DbDataReader r, int i) => Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
    static long? LongOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : Long(r, i);
    static int? IntOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : (int)Math.Clamp(Long(r, i), int.MinValue, int.MaxValue);
    static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    async Task<List<StampRow>> ReadStampsAsync(DbConnection connection, IReadOnlySet<Piece> missing, CancellationToken ct)
    {
        await using var cmd = Command(connection, StampSql(missing));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<StampRow>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new StampRow(Long(r, 0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? 0 : Long(r, 4), Str(r, 5), LongOrNull(r, 6), Str(r, 7), Str(r, 8), LongOrNull(r, 9)));
        return rows;
    }

    /// <summary>
    /// The views of <see cref="Pieces"/> the login cannot read, each probed with a statement that reads no row. Only after the
    /// whole stamp query was refused: a readable dictionary costs nothing extra.
    /// </summary>
    async Task<Dictionary<Piece, Exception>> ProbeAsync(DbConnection connection, CancellationToken ct)
    {
        var denied = new Dictionary<Piece, Exception>();
        foreach (var (piece, (view, _)) in Pieces)
        {
            await using var cmd = Command(connection, $"SELECT 1 FROM {_view(view)} WHERE 1 = 0");
            try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (IsDenied(ex)) { denied[piece] = ex; }
        }
        return denied;
    }

    public async Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection connection, CancellationToken ct)
    {
        NotesByConnection.AddOrUpdate(connection, []);
        List<StampRow> rows;
        try
        {
            rows = await ReadStampsAsync(connection, new HashSet<Piece>(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDenied(ex))
        {
            var denied = await ProbeAsync(connection, ct).ConfigureAwait(false);
            if (denied.Count == 0) throw; // ALL_OBJECTS or ALL_USERS itself: there is no catalog to show
            foreach (var (piece, why) in denied) Note(connection, DeniedNote(Pieces[piece].Effect, Pieces[piece].View, why));
            rows = await ReadStampsAsync(connection, denied.Keys.ToHashSet(), ct).ConfigureAwait(false);
        }
        return Assemble(rows);
    }

    // ------------------------------------------------------------------------------------------ details

    /// <summary>The bind-list sizes an id chunk is padded to: a few statement texts, each parsed once and reused (§14 metric 10).</summary>
    internal static readonly int[] IdBuckets = [16, 64, 256, 500];

    /// <summary>
    /// An id chunk (at most 500, under Oracle's 1,000-item IN limit) as <c>:i0, :i1, …</c> padded with -1 (no object id) up to
    /// the next of <see cref="IdBuckets"/>: literal ids would make every refresh's statements new text, each a hard parse.
    /// </summary>
    internal static (string List, Action<OracleCommand> Bind) IdBinds(IReadOnlyList<long> ids)
    {
        var size = IdBuckets.First(b => b >= ids.Count);
        var list = string.Join(", ", Enumerable.Range(0, size).Select(i => ":i" + i.ToString(CultureInfo.InvariantCulture)));
        return (list, cmd =>
        {
            for (var i = 0; i < size; i++)
                cmd.Parameters.Add(new OracleParameter("i" + i.ToString(CultureInfo.InvariantCulture), OracleDbType.Int64, i < ids.Count ? ids[i] : -1L, System.Data.ParameterDirection.Input));
        });
    }

    /// <summary>
    /// Runs one details read; a view the login cannot read becomes a note on that part (naming <paramref name="views"/>, the
    /// ones this read uses besides ALL_OBJECTS), not a failed refresh.
    /// </summary>
    async Task ReadAsync(DbConnection connection, string sql, Action<OracleCommand> bind, Piece piece, string views, Action<DbDataReader> row, CancellationToken ct)
    {
        try
        {
            await using var cmd = Command(connection, sql);
            bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) row(r);
        }
        catch (Exception ex) when (IsDenied(ex))
        {
            Note(connection, DeniedNote(Pieces[piece].Effect, views, ex));
        }
    }

    public async Task<MetadataBundle> GetDetailsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var columns = new List<ColumnRow>();
        var parms = new List<ParamRow>();
        var keys = new List<KeyRow>();
        var indexes = new List<IndexColumnRow>();
        var fks = new List<FkColumnRow>();
        var members = new List<MemberArg>();
        foreach (var chunk in objectIds.Distinct().Chunk(500))
        {
            var (ids, bind) = IdBinds(chunk);
            await ReadAsync(connection, $"""
                SELECT {NoFeedback} o.object_id, c.column_id, c.internal_column_id, c.column_name, c.data_type, c.data_type_owner, c.data_length, c.char_length, c.char_used,
                       c.data_precision, c.data_scale, c.nullable, c.identity_column, c.virtual_column, c.hidden_column, c.data_default_vc
                FROM {_view("ALL_OBJECTS")} o JOIN {_view("ALL_TAB_COLS")} c ON c.owner = o.owner AND c.table_name = o.object_name
                WHERE o.object_id IN ({ids}) AND o.object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW') AND c.user_generated = 'YES'
                ORDER BY o.object_id, c.internal_column_id
                """, bind, Piece.Columns, "ALL_TAB_COLS", r => columns.Add(Column(new CatalogColumn(Long(r, 0), IntOrNull(r, 1), (int)Long(r, 2), r.GetString(3), Str(r, 4) ?? "", Str(r, 5),
                    IntOrNull(r, 6), IntOrNull(r, 7), Str(r, 8), IntOrNull(r, 9), IntOrNull(r, 10), Str(r, 11) != "N", Str(r, 12) == "YES", Str(r, 13) == "YES",
                    Str(r, 14) == "YES", Str(r, 15)))), ct).ConfigureAwait(false);
            await ReadAsync(connection, $"""
                SELECT {NoFeedback} a.object_id, a.position, a.argument_name, a.data_type, a.type_owner, a.type_name, a.data_length, a.in_out, a.defaulted
                FROM {_view("ALL_ARGUMENTS")} a
                WHERE a.object_id IN ({ids}) AND a.package_name IS NULL AND a.data_level = 0 AND a.position > 0 AND a.argument_name IS NOT NULL
                ORDER BY a.object_id, a.position
                """, bind, Piece.Arguments, "ALL_ARGUMENTS", r => parms.Add(new ParamRow(Long(r, 0), (int)Long(r, 1), r.GetString(2),
                    TypeName(Str(r, 3), Str(r, 4), Str(r, 5), null, null, null, null, null), IntOrNull(r, 6), Output: Str(r, 7) is "OUT" or "IN/OUT",
                    HasDefault: Str(r, 8) == "Y")), ct).ConfigureAwait(false);
            await ReadAsync(connection, $"""
                SELECT {NoFeedback} o.object_id, k.constraint_name, k.constraint_type, k.status, k.deferred, k.validated, k.index_owner, k.index_name, cc.column_name
                FROM {_view("ALL_OBJECTS")} o
                JOIN {_view("ALL_CONSTRAINTS")} k ON k.owner = o.owner AND k.table_name = o.object_name AND k.constraint_type IN ('P', 'U')
                JOIN {_view("ALL_CONS_COLUMNS")} cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name AND cc.table_name = k.table_name
                WHERE o.object_id IN ({ids}) AND o.object_type = 'TABLE'
                ORDER BY o.object_id, k.constraint_name, cc.position
                """, bind, Piece.Constraints, "ALL_CONSTRAINTS or ALL_CONS_COLUMNS", r => keys.Add(new KeyRow(Long(r, 0), r.GetString(1), r.GetString(2) == "P", Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6), Str(r, 7),
                    r.GetString(8))), ct).ConfigureAwait(false);
            await ReadAsync(connection, $"""
                SELECT {NoFeedback} o.object_id, i.owner, i.index_name, i.uniqueness, i.index_type, i.status, ic.column_name,
                       CASE WHEN EXISTS (SELECT 1 FROM {_view("ALL_IND_EXPRESSIONS")} e WHERE e.index_owner = ic.index_owner AND e.index_name = ic.index_name
                         AND e.column_position = ic.column_position) THEN 1 ELSE 0 END
                FROM {_view("ALL_OBJECTS")} o
                JOIN {_view("ALL_INDEXES")} i ON i.table_owner = o.owner AND i.table_name = o.object_name AND i.index_type <> 'LOB'
                JOIN {_view("ALL_IND_COLUMNS")} ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
                WHERE o.object_id IN ({ids}) AND o.object_type = 'TABLE'
                ORDER BY o.object_id, i.owner, i.index_name, ic.column_position
                """, bind, Piece.Indexes, "ALL_INDEXES, ALL_IND_COLUMNS or ALL_IND_EXPRESSIONS", r => indexes.Add(new IndexColumnRow(Long(r, 0), r.GetString(1), r.GetString(2), Str(r, 3) == "UNIQUE", Str(r, 4) ?? "", Str(r, 5),
                    r.GetString(6), Long(r, 7) == 1)), ct).ConfigureAwait(false);
            await ReadAsync(connection, $"""
                SELECT {NoFeedback} o.object_id, k.constraint_name, cc.column_name, rc.column_name, ro.object_id
                FROM {_view("ALL_OBJECTS")} o
                JOIN {_view("ALL_CONSTRAINTS")} k ON k.owner = o.owner AND k.table_name = o.object_name AND k.constraint_type = 'R'
                JOIN {_view("ALL_CONSTRAINTS")} r ON r.owner = k.r_owner AND r.constraint_name = k.r_constraint_name
                JOIN {_view("ALL_CONS_COLUMNS")} cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name AND cc.table_name = k.table_name
                JOIN {_view("ALL_CONS_COLUMNS")} rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.table_name = r.table_name AND rc.position = cc.position
                JOIN {_view("ALL_OBJECTS")} ro ON ro.owner = r.owner AND ro.object_name = r.table_name AND ro.object_type = 'TABLE'
                WHERE o.object_id IN ({ids}) AND o.object_type = 'TABLE'
                ORDER BY o.object_id, k.constraint_name, cc.position
                """, bind, Piece.Constraints, "ALL_CONSTRAINTS or ALL_CONS_COLUMNS", r => fks.Add(new FkColumnRow(Long(r, 0), r.GetString(1), r.GetString(2), r.GetString(3), Long(r, 4))), ct).ConfigureAwait(false);
            // Package members (D4 S08), display only: each subprogram of a specification with its arguments, overloads apart.
            await ReadAsync(connection, $"""
                SELECT {NoFeedback} p.object_id, p.subprogram_id, p.procedure_name, a.position, a.argument_name, a.data_type, a.type_owner, a.type_name, a.in_out
                FROM {_view("ALL_PROCEDURES")} p
                LEFT JOIN {_view("ALL_ARGUMENTS")} a ON a.object_id = p.object_id AND a.subprogram_id = p.subprogram_id AND a.data_level = 0
                WHERE p.object_id IN ({ids}) AND p.object_type = 'PACKAGE' AND p.procedure_name IS NOT NULL
                ORDER BY p.object_id, p.subprogram_id, a.position
                """, bind, Piece.Procedures, "ALL_PROCEDURES or ALL_ARGUMENTS", r => members.Add(new MemberArg(Long(r, 0), (int)Long(r, 1), r.GetString(2), IntOrNull(r, 3),
                    Str(r, 4), Str(r, 5), Str(r, 6), Str(r, 7), Str(r, 8))), ct).ConfigureAwait(false);
        }
        return new MetadataBundle(columns, parms, AssembleIndexes(keys, indexes), AssembleFks(fks), AssembleMembers(members));
    }

    /// <summary>One row of the members read: a subprogram and one of its arguments (none: a subprogram without arguments).</summary>
    internal sealed record MemberArg(long ObjectId, int Subprogram, string Name, int? Position, string? Argument, string? DataType, string? TypeOwner, string? TypeName, string? InOut);

    /// <summary>
    /// <c>P(A IN NUMBER, B OUT VARCHAR2)</c>, <c>F(X IN NUMBER) RETURN NUMBER</c>, <c>Q</c>: one line per subprogram id, so
    /// overloads stay apart. Position 0 is a function's return value; a no-argument subprogram may still have one row with no
    /// argument name (the dictionary's placeholder), which adds nothing.
    /// </summary>
    internal static List<MemberRow> AssembleMembers(IEnumerable<MemberArg> rows) =>
        rows.GroupBy(m => (m.ObjectId, m.Subprogram))
            .OrderBy(g => g.Key.ObjectId).ThenBy(g => g.Key.Subprogram)
            .Select(g =>
            {
                var args = g.Where(a => a.Position > 0 && a.Argument != null).OrderBy(a => a.Position)
                    .Select(a => $"{a.Argument} {a.InOut ?? "IN"} {TypeName(a.DataType, a.TypeOwner, a.TypeName, null, null, null, null, null)}").ToList();
                var ret = g.FirstOrDefault(a => a.Position == 0);
                var text = g.First().Name + (args.Count > 0 ? $"({string.Join(", ", args)})" : "")
                           + (ret != null ? $" RETURN {TypeName(ret.DataType, ret.TypeOwner, ret.TypeName, null, null, null, null, null)}" : "");
                return new MemberRow(g.Key.ObjectId, g.Key.Subprogram, text);
            })
            .ToList();

    internal sealed record CatalogColumn(long ObjectId, int? ColumnId, int InternalId, string Name, string DataType, string? TypeOwner, int? DataLength, int? CharLength,
        string? CharUsed, int? Precision, int? Scale, bool Nullable, bool Identity, bool Virtual, bool Hidden, string? Default);

    /// <summary>
    /// An invisible column (hidden, user-made) has no COLUMN_ID: it gets an ordinal past every visible one (it is never in a
    /// <c>SELECT *</c> result to map back to) and says "invisible" in its type.
    /// </summary>
    internal const int InvisibleOrdinalBase = 100_000;

    internal static ColumnRow Column(CatalogColumn c)
    {
        var type = TypeName(c.DataType, c.TypeOwner, null, c.DataLength, c.CharLength, c.CharUsed, c.Precision, c.Scale);
        var invisible = c.Hidden || c.ColumnId == null;
        var text = c.DataType is "CHAR" or "NCHAR" or "VARCHAR2" or "NVARCHAR2" ? c.CharLength : c.DataLength;
        return new ColumnRow(c.ObjectId, invisible ? InvisibleOrdinalBase + c.InternalId : c.ColumnId!.Value, c.Name, invisible ? type + " invisible" : type,
            text, c.Precision, c.Scale, c.Nullable, c.Identity, c.Virtual, string.IsNullOrWhiteSpace(c.Default) ? null : c.Default.Trim());
    }

    /// <summary>
    /// The type as Oracle writes it: <c>VARCHAR2(40 CHAR)</c>, <c>NUMBER(12,2)</c>, <c>NUMBER</c>, <c>FLOAT(126)</c>,
    /// <c>RAW(16)</c>, <c>TIMESTAMP(6)</c> (the dictionary spells it), a user type as <c>OWNER.NAME</c>.
    /// </summary>
    internal static string TypeName(string? dataType, string? typeOwner, string? typeName, int? dataLength, int? charLength, string? charUsed, int? precision, int? scale)
    {
        if (typeName != null) return typeOwner != null ? $"{typeOwner}.{typeName}" : typeName;
        if (string.IsNullOrEmpty(dataType)) return "UNKNOWN";
        if (typeOwner != null && typeOwner != "SYS" && typeOwner != "PUBLIC") return $"{typeOwner}.{dataType}";
        var inv = CultureInfo.InvariantCulture;
        return dataType switch
        {
            "VARCHAR2" or "CHAR" when charLength is { } n => string.Create(inv, $"{dataType}({n}{(charUsed == "C" ? " CHAR" : charUsed == "B" ? " BYTE" : "")})"),
            "NVARCHAR2" or "NCHAR" when charLength is { } n => string.Create(inv, $"{dataType}({n})"),
            "RAW" when dataLength is { } n => string.Create(inv, $"RAW({n})"),
            "NUMBER" when precision is { } p => scale is { } s && s != 0 ? string.Create(inv, $"NUMBER({p},{s})") : string.Create(inv, $"NUMBER({p})"),
            "NUMBER" when scale is 0 => "INTEGER",
            "FLOAT" when precision is { } p => string.Create(inv, $"FLOAT({p})"),
            _ => dataType,
        };
    }

    internal sealed record KeyRow(long ObjectId, string Name, bool Primary, string? Status, string? Deferred, string? Validated, string? IndexOwner, string? IndexName, string Column);
    internal sealed record IndexColumnRow(long ObjectId, string Owner, string Name, bool Unique, string IndexType, string? Status, string Column, bool Expression);
    internal sealed record FkColumnRow(long ObjectId, string Name, string Column, string ReferencedColumn, long Referenced);

    /// <summary>
    /// Primary and unique constraints as keys (their columns from ALL_CONS_COLUMNS; a disabled, NOVALIDATE or initially
    /// deferred one never a grid row key), then the indexes that back no such constraint (a function-based, domain or
    /// unusable one never a row key; an expression column shown as "(expression)").
    /// </summary>
    internal static List<IndexRow> AssembleIndexes(IEnumerable<KeyRow> keys, IEnumerable<IndexColumnRow> indexes)
    {
        var list = new List<IndexRow>();
        var backing = new HashSet<(long, string, string)>();
        foreach (var g in keys.GroupBy(k => (k.ObjectId, k.Name)))
        {
            var k = g.First();
            if (k.IndexName != null) backing.Add((k.ObjectId, k.IndexOwner ?? "", k.IndexName));
            var notAKey = k.Status != "ENABLED" || k.Validated != "VALIDATED" || k.Deferred == "DEFERRED";
            list.Add(new IndexRow(k.ObjectId, k.Name, Unique: true, k.Primary, g.Select(x => x.Column).ToArray(), notAKey));
        }
        foreach (var g in indexes.GroupBy(i => (i.ObjectId, i.Owner, i.Name)))
        {
            if (backing.Contains(g.Key)) continue;
            var i = g.First();
            var notAKey = !i.Unique || i.IndexType.StartsWith("FUNCTION-BASED", StringComparison.Ordinal) || i.IndexType == "DOMAIN" || i.Status is "UNUSABLE" or "INVALID"
                          || g.Any(x => x.Expression);
            list.Add(new IndexRow(i.ObjectId, i.Name, i.Unique, Primary: false, g.Select(x => x.Expression ? "(expression)" : x.Column).ToArray(), notAKey));
        }
        return list;
    }

    internal static List<FkRow> AssembleFks(IEnumerable<FkColumnRow> rows) =>
        rows.GroupBy(f => (f.ObjectId, f.Name))
            .Select(g => new FkRow(g.Key.Name, g.Key.ObjectId, g.First().Referenced, g.Select(x => new FkColumnPair { Parent = x.Column, Referenced = x.ReferencedColumn }).ToArray()))
            .ToList();

    // ------------------------------------------------------------------------------------------ definitions

    /// <summary>The DBMS_METADATA object type for a kind; a view is asked for as a materialized view when it is one.</summary>
    internal static string? MetadataType(ObjectKind kind) => kind switch
    {
        ObjectKind.Table => "TABLE",
        ObjectKind.View => "VIEW",
        ObjectKind.Proc => "PROCEDURE",
        ObjectKind.Func or ObjectKind.Tvf => "FUNCTION",
        ObjectKind.Trigger => "TRIGGER",
        ObjectKind.Type => "TYPE",
        ObjectKind.Sequence => "SEQUENCE",
        ObjectKind.Synonym => "SYNONYM",
        ObjectKind.Package => "PACKAGE_SPEC",
        ObjectKind.PackageBody => "PACKAGE_BODY",
        _ => null,
    };

    /// <summary>The ALL_SOURCE types a PL/SQL kind's text is kept under: a type with its body, a package specification alone.</summary>
    internal static (string Unit, string Body)? SourceTypes(ObjectKind kind) => kind switch
    {
        ObjectKind.Proc => ("PROCEDURE", "PROCEDURE"),
        ObjectKind.Func => ("FUNCTION", "FUNCTION"),
        ObjectKind.Trigger => ("TRIGGER", "TRIGGER"),
        ObjectKind.Type => ("TYPE", "TYPE BODY"),
        ObjectKind.Package => ("PACKAGE", "PACKAGE"),
        ObjectKind.PackageBody => ("PACKAGE BODY", "PACKAGE BODY"),
        _ => null,
    };

    /// <summary>
    /// GET_DDL with the session's transform parameters set first, in the same round trip (they last for the session, and a
    /// package reset would clear them): no storage or segment clauses, a terminator (<c>;</c>, or <c>/</c> after PL/SQL) the
    /// splitter reads back. ORA-31603: the login may not call it for this object (another schema without
    /// SELECT_CATALOG_ROLE) — null, and the caller reads the source instead.
    /// </summary>
    const string GetDdlBlock = """
        DECLARE
          t VARCHAR2(30) := :kind;
        BEGIN
          IF t = 'VIEW' THEN
            SELECT NVL(MAX(REPLACE(object_type, ' ', '_')), 'VIEW') INTO t FROM ALL_OBJECTS
            WHERE owner = :owner AND object_name = :name AND object_type IN ('VIEW', 'MATERIALIZED VIEW');
          END IF;
          DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'PRETTY', TRUE);
          DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'SQLTERMINATOR', TRUE);
          DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'SEGMENT_ATTRIBUTES', FALSE);
          DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'STORAGE', FALSE);
          :ddl := DBMS_METADATA.GET_DDL(t, :name, :owner);
        END;
        """;

    /// <summary>ORA-31603 object not found (or not permitted), ORA-31604 / 31600 a transform the object type does not take.</summary>
    internal static bool NoGetDdl(Exception ex) => ex is OracleException { Number: 31603 or 31604 or 31600 } || IsDenied(ex);

    public async Task<string?> GetDefinitionAsync(DbConnection connection, ObjectRef obj, CancellationToken ct)
    {
        if (MetadataType(obj.Kind) is not { } kind) return null;
        try
        {
            await using var cmd = Command(connection, GetDdlBlock);
            cmd.Parameters.Add(new OracleParameter("kind", OracleDbType.Varchar2, kind, System.Data.ParameterDirection.Input));
            cmd.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2, obj.Schema, System.Data.ParameterDirection.Input));
            cmd.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2, obj.Name, System.Data.ParameterDirection.Input));
            var ddl = new OracleParameter("ddl", OracleDbType.Clob, System.Data.ParameterDirection.Output);
            cmd.Parameters.Add(ddl);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (ddl.Value is OracleClob { IsNull: false } clob)
            {
                using (clob) return Tidy(clob.Value);
            }
            return null;
        }
        catch (Exception ex) when (NoGetDdl(ex))
        {
            return await SourceDefinitionAsync(connection, obj, ct).ConfigureAwait(false);
        }
    }

    /// <summary>GET_DDL leads with a newline and indents its first line; the text starts at CREATE.</summary>
    static string Tidy(string ddl) => ddl.Trim('\n', '\r', ' ');

    /// <summary>
    /// Without GET_DDL: a PL/SQL unit from ALL_SOURCE (a type with its body) as CREATE OR REPLACE … ending in <c>/</c>, a view
    /// from ALL_VIEWS.TEXT. Anything else has no source the login can read: null.
    /// </summary>
    async Task<string?> SourceDefinitionAsync(DbConnection connection, ObjectRef obj, CancellationToken ct)
    {
        var name = $"{OracleIdentifiers.QuoteIfNeeded(obj.Schema)}.{OracleIdentifiers.QuoteIfNeeded(obj.Name)}";
        if (obj.Kind == ObjectKind.View)
        {
            await using var v = Command(connection, $"SELECT text FROM {_view("ALL_VIEWS")} WHERE owner = :owner AND view_name = :name");
            v.Parameters.Add(new OracleParameter("owner", obj.Schema));
            v.Parameters.Add(new OracleParameter("name", obj.Name));
            try
            {
                await using var r = await v.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0) ? $"CREATE OR REPLACE VIEW {name} AS\n{r.GetString(0).TrimEnd()};" : null;
            }
            catch (Exception ex) when (IsDenied(ex)) { return null; }
        }
        if (SourceTypes(obj.Kind) is not var (unit, body)) return null;
        await using var cmd = Command(connection, $"""
            SELECT type, text FROM {_view("ALL_SOURCE")} WHERE owner = :owner AND name = :name AND type IN (:t1, :t2) ORDER BY CASE type WHEN 'TYPE BODY' THEN 1 ELSE 0 END, line
            """);
        cmd.Parameters.Add(new OracleParameter("owner", obj.Schema));
        cmd.Parameters.Add(new OracleParameter("name", obj.Name));
        cmd.Parameters.Add(new OracleParameter("t1", unit));
        cmd.Parameters.Add(new OracleParameter("t2", body));
        var units = new List<(string Type, StringBuilder Text)>();
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var t = r.GetString(0);
                if (units.Count == 0 || units[^1].Type != t) units.Add((t, new StringBuilder()));
                units[^1].Text.Append(r.IsDBNull(1) ? "" : r.GetString(1));
            }
        }
        catch (Exception ex) when (IsDenied(ex)) { return null; }
        return units.Count == 0 ? null : string.Join("\n", units.Select(u => "CREATE OR REPLACE " + Qualify(u.Text.ToString().TrimEnd(), name) + "\n/"));
    }

    /// <summary>
    /// ALL_SOURCE keeps a unit as written after CREATE OR REPLACE (<c>PROCEDURE p (…)</c>), its owner not named: re-run as
    /// it is, it would create the unit in the user's own schema. The name (found on tokens, after the kind and an optional
    /// BODY) becomes <paramref name="qualified"/>.
    /// </summary>
    internal static string Qualify(string unit, string qualified)
    {
        var code = OracleTokenizer.Tokenize(unit).Where(t => !t.IsTrivia).ToList();
        var n = 0;
        while (n < code.Count && (code[n].Is("EDITIONABLE") || code[n].Is("NONEDITIONABLE"))) n++;
        if (n >= code.Count || !(code[n].Is("PROCEDURE") || code[n].Is("FUNCTION") || code[n].Is("TRIGGER") || code[n].Is("TYPE") || code[n].Is("PACKAGE"))) return unit;
        n++;
        if (n < code.Count && code[n].Is("BODY")) n++;
        if (n >= code.Count || code[n].Kind is not (OracleTokenKind.Word or OracleTokenKind.QuotedIdent)) return unit;
        var end = n;
        if (end + 2 < code.Count && code[end + 1].IsP(".") && code[end + 2].Kind is OracleTokenKind.Word or OracleTokenKind.QuotedIdent) end += 2;
        // One space before the name: ALL_SOURCE keeps the run of blanks CREATE OR REPLACE left in front of it.
        var kind = code[n - 1];
        return unit[..(kind.Offset + kind.Length)] + " " + qualified + unit[(code[end].Offset + code[end].Length)..];
    }

    /// <summary>Find usages and the dependency panel arrive with D4 S11 (ALL_DEPENDENCIES and the reference finder).</summary>
    internal const string DependenciesNotYet = "Dependencies for Oracle are not available in this build of Querithm yet.";

    public Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) =>
        Task.FromException<IReadOnlyList<(long Id, string? Definition)>>(new NotSupportedException(DependenciesNotYet));

    public Task<Dependency[]> GetDependenciesAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) =>
        Task.FromException<Dependency[]>(new NotSupportedException(DependenciesNotYet));

    /// <summary>
    /// GET_DDL already writes CREATE OR REPLACE for views and PL/SQL; a definition read another way gets it (on tokens) when its
    /// kind takes it. A table has no OR REPLACE: returned as it is.
    /// </summary>
    public string ToEditableScript(string definition, Capabilities caps) => Editable(definition);

    internal static string Editable(string definition)
    {
        var code = OracleTokenizer.Tokenize(definition).Where(t => !t.IsTrivia).ToList();
        if (code.Count < 2 || !code[0].Is("CREATE") || code[1].Is("OR")) return definition;
        var n = 1;
        while (n < code.Count && (code[n].Is("FORCE") || code[n].Is("NO") || code[n].Is("EDITIONABLE") || code[n].Is("NONEDITIONABLE") || code[n].Is("EDITIONING"))) n++;
        if (n >= code.Count || !(code[n].Is("VIEW") || code[n].Is("PROCEDURE") || code[n].Is("FUNCTION") || code[n].Is("TRIGGER") || code[n].Is("TYPE")
                                 || code[n].Is("SYNONYM") || code[n].Is("PACKAGE") || code[n].Is("PUBLIC")))
            return definition;
        var at = code[0].Offset + code[0].Length;
        return definition[..at] + " OR REPLACE" + definition[at..];
    }

    // ------------------------------------------------------------------------------------------ row counts

    /// <summary>
    /// ALL_TABLES.NUM_ROWS: the optimizer statistics' estimate (null until statistics were gathered), never COUNT(*). The
    /// time they were gathered is computed against the server's own clock (the dictionary keeps a DATE without a time zone),
    /// so it is right whatever the server's or this computer's zone.
    /// </summary>
    public async Task<RowCount[]> GetRowCountsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct)
    {
        var list = new List<RowCount>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var chunk in objectIds.Distinct().Chunk(500))
        {
            var (ids, bind) = IdBinds(chunk);
            await using var cmd = Command(connection, $"""
                SELECT {NoFeedback} o.object_id, t.num_rows, ROUND((SYSDATE - t.last_analyzed) * 86400000)
                FROM {_view("ALL_OBJECTS")} o JOIN {_view("ALL_TABLES")} t ON t.owner = o.owner AND t.table_name = o.object_name
                WHERE o.object_id IN ({ids}) AND o.object_type IN ('TABLE', 'MATERIALIZED VIEW')
                """);
            bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add(new RowCount { ObjectId = Long(r, 0), Rows = LongOrNull(r, 1), Estimated = true, AnalyzedAt = LongOrNull(r, 2) is { } ago ? now - ago : null });
        }
        return list.ToArray();
    }
}
