using System.Data.Common;
using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>One argument of a standalone routine as ALL_ARGUMENTS lists it (data level 0; position 0 is a function's result).</summary>
internal sealed record OracleArgument(int Position, string? Name, string? DataType, string? InOut, bool Defaulted,
    string? TypeOwner = null, string? TypeName = null, string? TypeSubname = null);

/// <summary>
/// Script as ▸ for Oracle. Packages since D4 S08, every other kind since D4 S11; scripts are text for a new tab, never run
/// here (§13 rule 7).
/// <list type="bullet">
/// <item>CREATE: <c>DBMS_METADATA.GET_DDL</c> (ALL_SOURCE / ALL_VIEWS without it), ending each statement in <c>;</c> and each
/// PL/SQL unit in <c>/</c>. ALTER (Modify → Apply): the same definition as CREATE OR REPLACE for views, procedures,
/// functions, triggers, types and synonyms; packages recompile (<c>ALTER PACKAGE … COMPILE</c>); tables, sequences and
/// materialized views have no CREATE OR REPLACE and refuse with the reason.</item>
/// <item>DROP names the object's real kind (a materialized view, a public synonym); Oracle 19c has no DROP … IF EXISTS.</item>
/// <item>SELECT: <c>FETCH FIRST 1000 ROWS ONLY</c> (12c+). ENABLE / DISABLE: <c>ALTER TRIGGER … ENABLE | DISABLE</c>.</item>
/// <item>EXECUTE: an anonymous block calling the routine with named arguments from the live ALL_ARGUMENTS (IN, OUT,
/// IN OUT, defaults): OUT and IN OUT values and a function's result come back as one implicit result row, a ref cursor
/// as a result set of its own (<c>DBMS_SQL.RETURN_RESULT</c>) or a line saying it was not opened. The Execute dialog's
/// values arrive as text and become typed literals here (<see cref="OracleDataEdit"/>), refused before the tab opens when
/// the parameter's type cannot take them.</item>
/// </list>
/// A package is scripted as a whole object — specification, body or both — never member by member. A body the login
/// cannot read (another schema without DEBUG) is left out with a comment saying so, and a script that would drop it says
/// it would be lost.
/// </summary>
public sealed class OracleScripter : IObjectScripter
{
    readonly Func<DbConnection, ObjectRef, CancellationToken, Task<string?>> _definition;
    readonly Func<string, string> _editable;
    readonly Func<DbConnection, ObjectRef, CancellationToken, Task<string?>> _objectType;
    readonly Func<DbConnection, ObjectRef, CancellationToken, Task<IReadOnlyList<OracleArgument>>> _arguments;

    public OracleScripter(OracleMetadata metadata)
        : this(metadata.GetDefinitionAsync, OracleMetadata.Editable, ObjectTypeAsync, ArgumentsAsync) { }

    /// <param name="definition">Tests: the definition reader (GET_DDL, or ALL_SOURCE without it).</param>
    /// <param name="editable">CREATE → CREATE OR REPLACE where the text lacks it.</param>
    internal OracleScripter(Func<DbConnection, ObjectRef, CancellationToken, Task<string?>> definition, Func<string, string> editable)
        : this(definition, editable, (_, _, _) => Task.FromResult<string?>(null), (_, _, _) => Task.FromResult<IReadOnlyList<OracleArgument>>([])) { }

    /// <param name="objectType">Tests: ALL_OBJECTS.OBJECT_TYPE of the object (null: not found).</param>
    /// <param name="arguments">Tests: the routine's ALL_ARGUMENTS rows.</param>
    internal OracleScripter(Func<DbConnection, ObjectRef, CancellationToken, Task<string?>> definition, Func<string, string> editable,
        Func<DbConnection, ObjectRef, CancellationToken, Task<string?>> objectType,
        Func<DbConnection, ObjectRef, CancellationToken, Task<IReadOnlyList<OracleArgument>>> arguments)
    {
        _definition = definition;
        _editable = editable;
        _objectType = objectType;
        _arguments = arguments;
    }

    internal const string NotRun = "A package is not run as a whole: run one of its subprograms. Scripting package members is not available yet.";
    internal const string BodyLeftOut = "-- The package body is not visible to this login (it needs DEBUG on the package), or there is none: it is not in this script.";
    internal const string BodyLost = "-- WARNING: DROP PACKAGE also drops the package body, which this login cannot read. It is not in this script and would be lost.";
    internal const string DropsBoth = "-- DROP PACKAGE drops the specification and its body.";
    internal const string NoReplaceTable = "Oracle has no CREATE OR REPLACE TABLE: change a table with ALTER TABLE, or script DROP and CREATE (its rows are lost).";
    internal const string NoReplaceSequence = "Oracle has no CREATE OR REPLACE SEQUENCE: change it with ALTER SEQUENCE (INCREMENT BY, MAXVALUE, CACHE, …), or script DROP and CREATE.";
    internal const string NoReplaceMView = "A materialized view cannot be replaced: change it with ALTER MATERIALIZED VIEW, or script DROP and CREATE.";
    internal const string RecycleBin = "-- DROP TABLE moves the table to the recycle bin (FLASHBACK TABLE … TO BEFORE DROP brings it back); add PURGE to drop it for good.";

    static string Name(ObjectRef o) => o.Kind == ObjectKind.Synonym && o.Schema == "PUBLIC"
        ? OracleIdentifiers.QuoteIfNeeded(o.Name)
        : $"{OracleIdentifiers.QuoteIfNeeded(o.Schema)}.{OracleIdentifiers.QuoteIfNeeded(o.Name)}";

    public Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, CancellationToken ct) =>
        ScriptAsync(connection, obj, kind, withBody: false, ct);

    public async Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, bool withBody, CancellationToken ct)
    {
        if (obj.Kind is ObjectKind.Package or ObjectKind.PackageBody) return await PackageAsync(connection, obj, kind, withBody, ct).ConfigureAwait(false);
        if (kind == ScriptKind.Execute) return await ExecuteAsync(connection, obj, [], ct).ConfigureAwait(false);
        var name = Name(obj);
        switch (kind)
        {
            case ScriptKind.Enable or ScriptKind.Disable:
                if (obj.Kind != ObjectKind.Trigger) throw new InvalidOperationException("Only triggers can be scripted as ENABLE / DISABLE.");
                return $"ALTER TRIGGER {name} {(kind == ScriptKind.Enable ? "ENABLE" : "DISABLE")};";
            case ScriptKind.SelectTop:
                if (obj.Kind is not (ObjectKind.Table or ObjectKind.View or ObjectKind.Synonym))
                    throw new InvalidOperationException($"{obj.Kind.ToString().ToLowerInvariant()}s cannot be selected from.");
                return $"SELECT *\nFROM {name}\nFETCH FIRST 1000 ROWS ONLY;";
            case ScriptKind.Drop:
                return await DropAsync(connection, obj, ct).ConfigureAwait(false);
            case ScriptKind.Alter:
            {
                if (obj.Kind == ObjectKind.Table) throw new NotSupportedException(NoReplaceTable);
                if (obj.Kind == ObjectKind.Sequence) throw new NotSupportedException(NoReplaceSequence);
                if (obj.Kind == ObjectKind.View && await IsMViewAsync(connection, obj, ct).ConfigureAwait(false)) throw new NotSupportedException(NoReplaceMView);
                return _editable(await RequiredAsync(connection, obj, ct).ConfigureAwait(false));
            }
            case ScriptKind.Create:
                return await RequiredAsync(connection, obj, ct).ConfigureAwait(false);
            case ScriptKind.DropCreate:
            {
                var drop = await DropAsync(connection, obj, ct).ConfigureAwait(false);
                return drop + "\n\n" + await RequiredAsync(connection, obj, ct).ConfigureAwait(false);
            }
            default:
                throw new NotSupportedException($"Script as {kind} is not available for Oracle.");
        }
    }

    async Task<bool> IsMViewAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) =>
        await _objectType(connection, obj, ct).ConfigureAwait(false) == "MATERIALIZED VIEW";

    async Task<string> DropAsync(DbConnection connection, ObjectRef obj, CancellationToken ct)
    {
        var name = Name(obj);
        return obj.Kind switch
        {
            ObjectKind.Table => $"{RecycleBin}\nDROP TABLE {name};",
            ObjectKind.View => await IsMViewAsync(connection, obj, ct).ConfigureAwait(false) ? $"DROP MATERIALIZED VIEW {name};" : $"DROP VIEW {name};",
            ObjectKind.Proc => $"DROP PROCEDURE {name};",
            ObjectKind.Func or ObjectKind.Tvf => $"DROP FUNCTION {name};",
            ObjectKind.Trigger => $"DROP TRIGGER {name};",
            // A type with dependents (tables, other types) refuses a plain DROP: FORCE is the user's call, never the script's.
            ObjectKind.Type => $"DROP TYPE {name};",
            ObjectKind.Sequence => $"DROP SEQUENCE {name};",
            ObjectKind.Synonym => obj.Schema == "PUBLIC" ? $"DROP PUBLIC SYNONYM {name};" : $"DROP SYNONYM {name};",
            _ => throw new NotSupportedException($"Script as DROP is not available for Oracle {obj.Kind.ToString().ToLowerInvariant()}s."),
        };
    }

    // ---------------------------------------------------------------------------------------------- packages (D4 S08)

    async Task<string> PackageAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, bool withBody, CancellationToken ct)
    {
        var body = obj.Kind == ObjectKind.PackageBody;
        var name = $"{OracleIdentifiers.QuoteIfNeeded(obj.Schema)}.{OracleIdentifiers.QuoteIfNeeded(obj.Name)}";
        switch (kind)
        {
            case ScriptKind.Alter:
                return $"ALTER PACKAGE {name} COMPILE {(body ? "BODY" : "SPECIFICATION")};";
            case ScriptKind.Drop:
                return body ? $"DROP PACKAGE BODY {name};" : $"{DropsBoth}\nDROP PACKAGE {name};";
            case ScriptKind.Create:
            case ScriptKind.DropCreate:
            {
                var self = _editable(await RequiredAsync(connection, obj, ct).ConfigureAwait(false));
                var parts = new List<string>();
                if (kind == ScriptKind.DropCreate) parts.Add(body ? $"DROP PACKAGE BODY {name};" : $"DROP PACKAGE {name};");
                parts.Add(self);
                if (!body && (withBody || kind == ScriptKind.DropCreate))
                {
                    var bodyRef = new ObjectRef { ProfileId = obj.ProfileId, Database = obj.Database, Schema = obj.Schema, Name = obj.Name, Kind = ObjectKind.PackageBody };
                    if (await _definition(connection, bodyRef, ct).ConfigureAwait(false) is { } b) parts.Add(_editable(b));
                    else parts.Insert(0, kind == ScriptKind.DropCreate ? BodyLost : BodyLeftOut);
                }
                return string.Join("\n\n", parts);
            }
            default:
                throw new NotSupportedException(NotRun);
        }
    }

    async Task<string> RequiredAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) =>
        await _definition(connection, obj, ct).ConfigureAwait(false) is { } d
            ? d
            : throw new InvalidOperationException($"No definition is available for {obj.Schema}.{obj.Name}: it no longer exists, or this login cannot read its source.");

    // ---------------------------------------------------------------------------------------------- execute

    /// <summary>The Execute dialog's call: <paramref name="values"/> by parameter name (as ALL_ARGUMENTS spells it).</summary>
    public async Task<string> ScriptExecuteAsync(DbConnection connection, ObjectRef obj, IReadOnlyList<ScriptArgument> values, CancellationToken ct)
    {
        if (obj.Kind is ObjectKind.Package or ObjectKind.PackageBody) throw new NotSupportedException(NotRun);
        return await ExecuteAsync(connection, obj, values, ct).ConfigureAwait(false);
    }

    async Task<string> ExecuteAsync(DbConnection connection, ObjectRef obj, IReadOnlyList<ScriptArgument> values, CancellationToken ct)
    {
        if (obj.Kind is not (ObjectKind.Proc or ObjectKind.Func or ObjectKind.Tvf))
            throw new InvalidOperationException(obj.Kind == ObjectKind.Trigger ? "A trigger cannot be executed: it runs when its event happens." : "Only procedures and functions can be executed.");
        var args = await _arguments(connection, obj, ct).ConfigureAwait(false);
        return ExecuteBlock(Name(obj), obj.Kind != ObjectKind.Proc, args, values);
    }

    /// <summary>How a parameter's value is held in the block and whether it can come back in the result row.</summary>
    internal readonly record struct Slot(string Declared, bool Sql, bool Boolean, bool Cursor);

    /// <summary>
    /// The local variable type for an OUT / IN OUT parameter or a function result: text types at their PL/SQL maximum,
    /// SYS_REFCURSOR for a ref cursor, the named type for an object / collection / package type. Null when the argument's
    /// type cannot be declared from ALL_ARGUMENTS (an anonymous PL/SQL record).
    /// </summary>
    internal static Slot? SlotOf(OracleArgument a)
    {
        var t = (a.DataType ?? "").Trim().ToUpperInvariant();
        string? named = a.TypeName == null ? null
            : string.Join(".", new[] { a.TypeOwner, a.TypeName, a.TypeSubname }.Where(p => !string.IsNullOrEmpty(p)).Select(p => OracleIdentifiers.QuoteIfNeeded(p!)));
        return t switch
        {
            "VARCHAR2" or "VARCHAR" or "CHAR" or "LONG" => new Slot("VARCHAR2(32767)", true, false, false),
            "NVARCHAR2" or "NCHAR" => new Slot("NVARCHAR2(16383)", true, false, false),
            "NUMBER" or "FLOAT" or "INTEGER" or "BINARY_INTEGER" or "PLS_INTEGER" or "NATURAL" or "POSITIVE" => new Slot("NUMBER", true, false, false),
            "BINARY_FLOAT" or "BINARY_DOUBLE" or "DATE" or "TIMESTAMP" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP WITH LOCAL TIME ZONE"
                or "INTERVAL DAY TO SECOND" or "INTERVAL YEAR TO MONTH" or "CLOB" or "NCLOB" or "BLOB" => new Slot(t, true, false, false),
            "RAW" or "LONG RAW" => new Slot("RAW(32767)", true, false, false),
            "ROWID" or "UROWID" => new Slot("UROWID", true, false, false),
            "BOOLEAN" or "PL/SQL BOOLEAN" => new Slot("BOOLEAN", false, true, false),
            "REF CURSOR" => new Slot("SYS_REFCURSOR", false, false, true),
            _ when named != null => new Slot(named, false, false, false),
            _ => null,
        };
    }

    /// <summary>ALL_ARGUMENTS' type in the terms the grid's text conversion reads (PL/SQL BOOLEAN is BOOLEAN).</summary>
    static string LiteralType(OracleArgument a) => (a.DataType ?? "").ToUpperInvariant() switch
    {
        "PL/SQL BOOLEAN" => "BOOLEAN",
        "BINARY_INTEGER" or "PLS_INTEGER" or "NATURAL" or "POSITIVE" => "NUMBER",
        // A parameter has no length (ALL_ARGUMENTS gives none): PL/SQL's own limit, not a bare column's CHAR(1).
        "CHAR" => "CHAR(32767 BYTE)",
        "NCHAR" => "NCHAR(32767)",
        var t => t,
    };

    static string Mode(OracleArgument a) => a.InOut switch { "OUT" => "OUT", "IN/OUT" => "IN OUT", _ => "IN" };

    static string Describe(OracleArgument a) =>
        a.TypeName != null ? string.Join(".", new[] { a.TypeOwner, a.TypeName, a.TypeSubname }.Where(p => !string.IsNullOrEmpty(p))) : a.DataType ?? "?";

    /// <summary>
    /// The anonymous block for a call: named notation (<c>P_A =&gt; value</c>), one argument per line with its type and
    /// whether it is required in a comment after the separator. An IN value the user gave is a typed literal (<see
    /// cref="OracleDataEdit.PreviewLiteral"/>, refused when the type cannot take it); without one an IN parameter with a
    /// default is left out (its default applies, listed as a comment) and a required one is NULL. OUT and IN OUT go
    /// through local variables (<c>Q$name</c>, so a SELECT's column names cannot capture them); an IN OUT value is the
    /// variable's start value. The values that can come back through SQL return as one implicit result row; booleans as
    /// TRUE / FALSE text; ref cursors each as their own result set.
    /// </summary>
    internal static string ExecuteBlock(string routine, bool function, IReadOnlyList<OracleArgument> args, IReadOnlyList<ScriptArgument> values)
    {
        var byName = values.GroupBy(v => v.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        var result = function ? args.FirstOrDefault(a => a.Position == 0) : null;
        var parameters = args.Where(a => a.Position > 0 && !string.IsNullOrEmpty(a.Name)).OrderBy(a => a.Position).ToList();
        if (values.FirstOrDefault(v => !parameters.Any(p => p.Name == v.Name)) is { } unknown)
            throw new ArgumentException($"{routine} has no parameter {unknown.Name}.");

        var declare = new List<string>();
        var after = new List<string>();
        var returned = new List<(string Expr, string Column)>();
        var cursors = new List<(string Var, string Column)>();
        var notShown = new List<string>();
        var n = 0;
        string Local(string name)
        {
            var v = "Q$" + name;
            return OracleIdentifiers.CanBeBare(v) ? v : OracleIdentifiers.Quote("q$" + n++.ToString(CultureInfo.InvariantCulture) + "_" + name);
        }
        void Keep(string var, Slot s, string column, string label)
        {
            if (s.Cursor) cursors.Add((var, column));
            else if (s.Boolean)
            {
                var text = OracleIdentifiers.Quote(((string)var).Trim('"') + "$text");
                declare.Add($"  {text} VARCHAR2(5);");
                after.Add($"  {text} := CASE WHEN {var} THEN 'TRUE' WHEN NOT {var} THEN 'FALSE' END;");
                returned.Add((text, column));
            }
            else if (s.Sql) returned.Add((var, column));
            else notShown.Add($"{label} ({s.Declared})");
        }

        string? resultVar = null;
        if (result != null)
        {
            var slot = SlotOf(result) ?? throw new NotSupportedException($"{routine} returns {Describe(result)}, a type the call cannot be declared for: call it from your own block.");
            resultVar = Local("RESULT");
            declare.Add($"  {resultVar} {slot.Declared}; -- the function's result ({CommentText.Safe(Describe(result))})");
            Keep(resultVar, slot, "RESULT", "the result");
        }

        var lines = new List<(string Text, string Comment)>();
        var omitted = new List<string>();
        foreach (var p in parameters)
        {
            var name = p.Name!;
            var formal = OracleIdentifiers.QuoteIfNeeded(name);
            var mode = Mode(p);
            byName.TryGetValue(name, out var given);
            if (mode == "IN")
            {
                if (given == null || given.UseDefault)
                {
                    if (p.Defaulted) { omitted.Add($"    -- {formal} => …, -- {CommentText.Safe(Describe(p))}, optional: its default applies"); continue; }
                    lines.Add(($"{formal} => NULL", $"{Describe(p)}, required"));
                    continue;
                }
                lines.Add(($"{formal} => {Literal(p, given, routine)}", $"{Describe(p)}{(p.Defaulted ? ", optional" : ", required")}"));
                continue;
            }
            var slot = SlotOf(p) ?? throw new NotSupportedException($"Parameter {name} of {routine} is {Describe(p)}, a type the call cannot be declared for: call it from your own block.");
            var local = Local(name);
            var start = mode == "IN OUT" && given is { UseDefault: false } ? " := " + Literal(p, given, routine) : "";
            declare.Add($"  {local} {slot.Declared}{start}; -- {mode} {CommentText.Safe(Describe(p))}");
            lines.Add(($"{formal} => {local}", $"{mode} {Describe(p)}"));
            Keep(local, slot, name, name);
        }

        var sb = new StringBuilder();
        var hasResultRow = returned.Count > 0;
        if (hasResultRow) declare.Add("  Q$OUT SYS_REFCURSOR;");
        if (declare.Count > 0) sb.Append("DECLARE\n").Append(string.Join("\n", declare)).Append('\n');
        sb.Append("BEGIN\n");
        var call = resultVar != null ? $"  {resultVar} := {routine}" : $"  {routine}";
        if (lines.Count == 0 && omitted.Count == 0) sb.Append(call).Append(function ? "();\n" : ";\n");
        else
        {
            sb.Append(call).Append("(\n");
            for (var i = 0; i < lines.Count; i++)
                sb.Append("    ").Append(lines[i].Text).Append(i < lines.Count - 1 ? "," : "").Append(" -- ").Append(CommentText.Safe(lines[i].Comment)).Append('\n');
            foreach (var o in omitted) sb.Append(o).Append('\n');
            sb.Append("  );\n");
        }
        foreach (var a in after) sb.Append(a).Append('\n');
        if (hasResultRow)
        {
            sb.Append("  OPEN Q$OUT FOR SELECT ")
              .Append(string.Join(", ", returned.Select(r => $"{r.Expr} AS {OracleIdentifiers.Quote(r.Column)}")))
              .Append(" FROM DUAL;\n  DBMS_SQL.RETURN_RESULT(Q$OUT);\n");
        }
        foreach (var (v, column) in cursors)
        {
            var said = Literal(column + " was not opened by the call, so it returns no rows.");
            sb.Append($"  IF {v}%ISOPEN THEN DBMS_SQL.RETURN_RESULT({v}); ELSE DBMS_OUTPUT.PUT_LINE({said}); END IF;\n");
        }
        if (notShown.Count > 0) sb.Append($"  -- Not returned (not a SQL type): {CommentText.Safe(string.Join(", ", notShown))}\n");
        sb.Append("END;\n/");
        return sb.ToString();
    }

    static string Literal(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>The value as a literal of the parameter's type; a value the type cannot take is refused with the reason.</summary>
    static string Literal(OracleArgument p, GridValue value, string routine)
    {
        if (value.IsNull || value.Text == null) return "NULL";
        var type = LiteralType(p);
        var t = OracleColumnType.Parse(type);
        if (t.Kind is OracleValueKind.Object or OracleValueKind.Blob or OracleValueKind.BFile or OracleValueKind.LongRaw or OracleValueKind.Xml
            or OracleValueKind.Json or OracleValueKind.Vector or OracleValueKind.RowId)
            throw new ArgumentException($"Parameter {p.Name} of {routine} is {Describe(p)}: the Execute dialog takes text, numbers, dates and RAW values only. Script the call and set it in the block.");
        try { OracleDataEdit.ToParameter(t, value.Text); }
        catch (FormatException ex) { throw new ArgumentException($"{p.Name}: {ex.Message}", ex); }
        return new OracleDataEdit().PreviewLiteral(type, value);
    }

    // ---------------------------------------------------------------------------------------------- catalog reads

    static OracleCommand Command(DbConnection c, string sql)
    {
        var cmd = (OracleCommand)c.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        return cmd;
    }

    /// <summary>ALL_OBJECTS.OBJECT_TYPE (a view may be a materialized view); null when the object is gone.</summary>
    static async Task<string?> ObjectTypeAsync(DbConnection c, ObjectRef o, CancellationToken ct)
    {
        await using var cmd = Command(c, $"""
            SELECT {OracleMetadata.NoFeedback} object_type FROM ALL_OBJECTS WHERE owner = :owner AND object_name = :name
              AND object_type NOT IN ('PACKAGE BODY', 'TYPE BODY', 'INDEX', 'LOB') ORDER BY CASE object_type WHEN 'MATERIALIZED VIEW' THEN 0 ELSE 1 END
            FETCH FIRST 1 ROWS ONLY
            """);
        cmd.Parameters.Add(new OracleParameter("owner", o.Schema));
        cmd.Parameters.Add(new OracleParameter("name", o.Name));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>The routine's top-level arguments (DATA_LEVEL 0), in position order; position 0 is a function's result.</summary>
    static async Task<IReadOnlyList<OracleArgument>> ArgumentsAsync(DbConnection c, ObjectRef o, CancellationToken ct)
    {
        await using var cmd = Command(c, $"""
            SELECT {OracleMetadata.NoFeedback} a.position, a.argument_name, a.data_type, a.in_out, a.defaulted, a.type_owner, a.type_name, a.type_subname
            FROM ALL_OBJECTS o JOIN ALL_ARGUMENTS a ON a.object_id = o.object_id
            WHERE o.owner = :owner AND o.object_name = :name AND o.object_type IN ('PROCEDURE', 'FUNCTION') AND a.package_name IS NULL AND a.data_level = 0
            ORDER BY a.position
            """);
        cmd.Parameters.Add(new OracleParameter("owner", o.Schema));
        cmd.Parameters.Add(new OracleParameter("name", o.Name));
        var list = new List<OracleArgument>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new OracleArgument(Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture), S(1), S(2), S(3), S(4) == "Y", S(5), S(6), S(7)));
        // A procedure without arguments lists one row with no name and no type: no parameter.
        return list.Where(a => a.Position == 0 || a.Name != null).ToList();
    }
}
