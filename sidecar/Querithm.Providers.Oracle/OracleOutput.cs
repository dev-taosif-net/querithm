using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>
/// What a batch says besides its rows (D4 S04): DBMS_OUTPUT lines, read back with <c>DBMS_OUTPUT.GET_LINES</c> after each
/// batch while server output is on for the connection, and the compile errors of a PL/SQL unit a <c>CREATE</c> made —
/// ODP.NET returns success for ORA-24344 ("success with compilation error") and raises nothing, so they are read from
/// ALL_ERRORS.
/// </summary>
internal static class OracleOutput
{
    /// <summary>
    /// Lines read per batch at most: half of the 20,000 server messages a run forwards (MessageBatcher.MaxMessages), so the
    /// warning that says so, and the messages of the script's later statements, are still shown. The rest of the server's
    /// buffer is discarded instead of crossing the network.
    /// </summary>
    public const int MaxLinesPerBatch = 10_000;
    /// <summary>Lines per GET_LINES call; each line may be 32,767 bytes, which the driver allocates per element.</summary>
    public const int LinesPerCall = 200;

    /// <summary>Whether DBMS_OUTPUT is enabled on a connection, as Querithm last set it (a new connection: off).</summary>
    static readonly ConditionalWeakTable<DbConnection, StrongBox<bool>> Enabled = new();

    public static bool IsEnabled(DbConnection connection) => Enabled.TryGetValue(connection, out var b) && b.Value;

    public static async Task SetAsync(DbConnection connection, bool on, CancellationToken ct)
    {
        if (IsEnabled(connection) == on) return;
        await using var cmd = connection.CreateCommand();
        // ENABLE(NULL): an unlimited buffer on the server; what Querithm reads back is bounded by MaxLinesPerBatch.
        cmd.CommandText = on ? "BEGIN DBMS_OUTPUT.ENABLE(NULL); END;" : "BEGIN DBMS_OUTPUT.DISABLE; END;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        Enabled.AddOrUpdate(connection, new StrongBox<bool>(on));
    }

    public static async Task<IReadOnlyList<ProviderMessage>> AfterBatchAsync(DbConnection connection, string batchText, CancellationToken ct)
    {
        var list = new List<ProviderMessage>();
        if (CreatedUnit(batchText) is { } unit) list.AddRange(await CompileErrorsAsync(connection, unit, ct).ConfigureAwait(false));
        if (IsEnabled(connection)) list.AddRange(await LinesAsync(connection, ct).ConfigureAwait(false));
        return list;
    }

    /// <summary>After a failed batch: what it printed before it raised, so the lines never surface after a later statement.</summary>
    public static async Task<IReadOnlyList<ProviderMessage>> AfterFailureAsync(DbConnection connection, CancellationToken ct) =>
        IsEnabled(connection) ? await LinesAsync(connection, ct).ConfigureAwait(false) : [];

    /// <summary>The buffered DBMS_OUTPUT lines (an empty line for a NULL one), at most <see cref="MaxLinesPerBatch"/>.</summary>
    static async Task<List<ProviderMessage>> LinesAsync(DbConnection connection, CancellationToken ct)
    {
        var list = new List<ProviderMessage>();
        await using var cmd = (OracleCommand)connection.CreateCommand();
        cmd.CommandText = "BEGIN DBMS_OUTPUT.GET_LINES(:lines, :n); END;";
        cmd.BindByName = true;
        var lines = new OracleParameter("lines", OracleDbType.Varchar2, ParameterDirection.Output)
        {
            CollectionType = OracleCollectionType.PLSQLAssociativeArray, Size = LinesPerCall,
            ArrayBindSize = Enumerable.Repeat(32767, LinesPerCall).ToArray(),
        };
        var n = new OracleParameter("n", OracleDbType.Int32, ParameterDirection.InputOutput);
        cmd.Parameters.Add(lines);
        cmd.Parameters.Add(n);
        while (true)
        {
            n.Value = LinesPerCall;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            var got = n.Value is OracleDecimal d ? d.ToInt32() : Convert.ToInt32(n.Value, CultureInfo.InvariantCulture);
            var values = lines.Value as OracleString[] ?? [];
            for (var i = 0; i < got && i < values.Length; i++)
            {
                if (list.Count == MaxLinesPerBatch)
                {
                    await DiscardAsync(connection, ct).ConfigureAwait(false);
                    list.Add(new ProviderMessage(Severity.Warning,
                        $"DBMS_OUTPUT printed more than {MaxLinesPerBatch:N0} lines in this statement; the rest were discarded."));
                    return list;
                }
                list.Add(new ProviderMessage(Severity.Info, values[i].IsNull ? "" : values[i].Value));
            }
            if (got < LinesPerCall) return list;
        }
    }

    /// <summary>DISABLE purges the buffer; ENABLE turns output back on for the next statement.</summary>
    static async Task DiscardAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "BEGIN DBMS_OUTPUT.DISABLE; DBMS_OUTPUT.ENABLE(NULL); END;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A unit ALL_ERRORS knows: owner (null = the current schema), name and type, and the batch line its source starts on.</summary>
    internal sealed record Unit(string? Owner, string Name, string Type, int SourceLine);

    /// <summary>
    /// The stored unit a <c>CREATE [OR REPLACE] [[NON]EDITIONABLE] PROCEDURE | FUNCTION | PACKAGE [BODY] | TRIGGER | TYPE
    /// [BODY] | [FORCE] VIEW</c> batch makes, read from its tokens; null for any other batch. Its stored source starts at
    /// the kind keyword, which is where ALL_ERRORS counts line 1.
    /// </summary>
    internal static Unit? CreatedUnit(string batchText)
    {
        var code = OracleTokenizer.Tokenize(batchText).Where(t => !t.IsTrivia).ToList();
        if (code.Count < 3 || !code[0].Is("CREATE")) return null;
        var i = 1;
        if (code[i].Is("OR") && i + 1 < code.Count && code[i + 1].Is("REPLACE")) i += 2;
        var forceView = false;
        while (i < code.Count && (code[i].Is("EDITIONABLE") || code[i].Is("NONEDITIONABLE") || code[i].Is("EDITIONING") || code[i].Is("FORCE") || code[i].Is("NOFORCE")))
        {
            forceView |= code[i].Is("FORCE");
            i++;
        }
        if (i >= code.Count) return null;
        var kind = code[i];
        string type;
        if (kind.Is("PROCEDURE") || kind.Is("FUNCTION") || kind.Is("TRIGGER")) type = kind.Text.ToUpperInvariant();
        else if (kind.Is("PACKAGE") || kind.Is("TYPE"))
        {
            type = kind.Text.ToUpperInvariant();
            if (i + 1 < code.Count && code[i + 1].Is("BODY")) { type += " BODY"; i++; }
        }
        else if (kind.Is("VIEW") && forceView) type = "VIEW";
        else return null;
        i++;
        if (i < code.Count && code[i].Is("IF")) // IF NOT EXISTS (23ai)
        {
            while (i < code.Count && (code[i].Is("IF") || code[i].Is("NOT") || code[i].Is("EXISTS"))) i++;
        }
        if (i >= code.Count || code[i].Kind is not (OracleTokenKind.Word or OracleTokenKind.QuotedIdent)) return null;
        string? owner = null;
        var name = OracleIdentifiers.Identity(code[i]);
        if (i + 2 < code.Count && code[i + 1].IsP(".") && code[i + 2].Kind is OracleTokenKind.Word or OracleTokenKind.QuotedIdent)
        {
            owner = name;
            name = OracleIdentifiers.Identity(code[i + 2]);
        }
        // A trigger's errors count lines from its PL/SQL block (DECLARE / BEGIN, or COMPOUND TRIGGER), not from TRIGGER.
        var sourceLine = kind.Line;
        if (type == "TRIGGER")
            for (var j = i + 1; j < code.Count; j++)
                if (code[j].Is("DECLARE") || code[j].Is("BEGIN") || code[j].Is("COMPOUND")) { sourceLine = code[j].Line; break; }
        return new Unit(owner, name, type, sourceLine);
    }

    const string ErrorsSql =
        "SELECT line, position, text, attribute, message_number FROM all_errors " +
        "WHERE owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND name = :name AND type = :type ORDER BY sequence";

    /// <summary>
    /// The unit's compile errors and warnings, each at its line in the batch (and column), then one line that says the
    /// unit was created invalid. Nothing when it compiled.
    /// </summary>
    static async Task<List<ProviderMessage>> CompileErrorsAsync(DbConnection connection, Unit unit, CancellationToken ct)
    {
        var list = new List<ProviderMessage>();
        await using var cmd = (OracleCommand)connection.CreateCommand();
        cmd.CommandText = ErrorsSql;
        cmd.BindByName = true;
        cmd.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2, (object?)unit.Owner ?? DBNull.Value, ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2, unit.Name, ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("type", OracleDbType.Varchar2, unit.Type, ParameterDirection.Input));
        var errors = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false) && list.Count < 1000)
            {
                var line = r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture);
                var position = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
                var text = r.IsDBNull(2) ? "" : OracleErrors.Clean(r.GetString(2));
                var error = !string.Equals(r.IsDBNull(3) ? "ERROR" : r.GetString(3), "WARNING", StringComparison.OrdinalIgnoreCase);
                if (error) errors++;
                int? batchLine = line > 0 ? unit.SourceLine + line - 1 : null;
                if (batchLine != null) text += string.Create(CultureInfo.InvariantCulture, $" (line {batchLine}, column {position})");
                list.Add(new ProviderMessage(error ? Severity.Error : Severity.Warning, text, Line: batchLine, Procedure: unit.Name,
                    Number: r.IsDBNull(4) ? null : Convert.ToInt32(r.GetValue(4), CultureInfo.InvariantCulture),
                    Column: batchLine != null && position > 0 ? position : null));
            }
        }
        if (errors > 0)
            list.Add(new ProviderMessage(Severity.Warning,
                $"{unit.Type} {unit.Name} was created with compilation errors (ORA-24344) and is invalid until they are fixed.", Procedure: unit.Name, Number: 24344));
        return list;
    }
}
