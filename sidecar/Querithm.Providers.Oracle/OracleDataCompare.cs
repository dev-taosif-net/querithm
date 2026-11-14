using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>
/// Data compare on Oracle (D4 S11). Keys are read in an order the .NET merge reproduces: text by its binary code
/// (<c>NLSSORT(x, 'NLS_SORT=BINARY')</c> in the ORDER BY itself, so the session's NLS_SORT — which ODP.NET derives from this
/// computer's language — never decides it and nothing about the pooled session changes); numbers exactly (NUMBER's 38
/// digits, through Core's exact comparer); DATE and TIMESTAMP by value. CHAR is padded text; intervals, ROWIDs, LOBs, LONG,
/// XMLTYPE, JSON, VECTOR and object types are never keys, and LOB-like values are not compared at all (the grid's text of
/// them is partial or a placeholder). Oracle stores '' as NULL, so the two are one value (<see cref="EmptyTextIsNull"/>).
/// A custom query must be one SELECT (or WITH … SELECT) without FOR UPDATE; it runs in a READ ONLY transaction. The sync
/// script is one anonymous PL/SQL block: Oracle undoes every change of a block that fails, so nothing is half-applied, and
/// a session in autocommit commits it once at its end.
/// </summary>
public sealed class OracleDataCompare : IDataCompareDialect
{
    public ValueKind KindOf(string? columnType)
    {
        var t = OracleColumnType.Parse(columnType);
        return t.Kind switch
        {
            OracleValueKind.Number => t.Scale == 0 ? ValueKind.Integer : ValueKind.Decimal,
            OracleValueKind.BinaryFloat or OracleValueKind.BinaryDouble => ValueKind.Float,
            OracleValueKind.Date or OracleValueKind.Timestamp or OracleValueKind.TimestampLtz => ValueKind.DateTime,
            OracleValueKind.TimestampTz => ValueKind.DateTimeOffset,
            OracleValueKind.Char or OracleValueKind.NChar => ValueKind.PaddedText,
            OracleValueKind.VarChar or OracleValueKind.NVarChar => ValueKind.Text,
            OracleValueKind.Raw => ValueKind.Binary,
            OracleValueKind.Boolean => ValueKind.Bool,
            _ => ValueKind.Unordered,
        };
    }

    public string OrderExpression(string columnExpr, ValueKind kind) => kind switch
    {
        ValueKind.Text or ValueKind.PaddedText => $"NLSSORT({columnExpr}, 'NLS_SORT=BINARY')",
        _ => columnExpr,
    };

    /// <summary>VARCHAR2 compares without padding; CHAR is <see cref="ValueKind.PaddedText"/> by its kind.</summary>
    public bool PadsTrailingSpaces => false;

    public bool EmptyTextIsNull => true;

    public bool QueryTypesFromProvider => true;

    /// <summary>
    /// ORA-01466: Oracle maps DDL time to SCNs only every few seconds, so a READ ONLY transaction started just after a CREATE
    /// or ALTER may refuse the table (CI 43581675301: a compare right after its tables were created).
    /// </summary>
    public bool SnapshotTooOld(Exception ex) => ex is global::Oracle.ManagedDataAccess.Client.OracleException o && OracleErrors.Code(o) == 1466;

    public string? ValueRefusal(string? columnType)
    {
        var t = OracleColumnType.Parse(columnType);
        return t.Kind switch
        {
            OracleValueKind.Clob or OracleValueKind.Blob => $"{t.Name} values are read in part, so they are not compared",
            OracleValueKind.Long or OracleValueKind.LongRaw => $"{t.Name} values cannot be read in a compare",
            OracleValueKind.BFile => "a BFILE points to a file on the server",
            OracleValueKind.Xml or OracleValueKind.Json or OracleValueKind.Vector => $"{t.Name} values are not compared",
            OracleValueKind.Object => $"{columnType} is an object or collection type, which Querithm does not read",
            _ => null,
        };
    }

    static List<OracleToken> Code(string sql)
    {
        var statements = OracleSplitter.Split(sql).Where(s => s.Code.Any()).ToList();
        return statements.Count == 1 ? statements[0].Code.ToList() : [];
    }

    public string? QueryRefusal(string sql)
    {
        var statements = OracleSplitter.Split(sql).Where(s => s.Code.Any()).ToList();
        if (statements.Count == 0) return "The query is empty.";
        if (statements.Count > 1) return "A compared query must be exactly one statement.";
        var st = statements[0];
        if (st.Kind == OracleStatementKind.ClientCommand) return "A compared query must be a SELECT, not a SQL*Plus command.";
        var code = st.Code.ToList();
        if (code.Count > 1 && code[0].Is("WITH") && (code[1].Is("FUNCTION") || code[1].Is("PROCEDURE"))) return "A compared query cannot declare PL/SQL (WITH FUNCTION).";
        if (st.Kind == OracleStatementKind.PlSql) return "A compared query must be a SELECT, not a PL/SQL block.";
        while (code.Count > 0 && code[^1].IsP(";")) code.RemoveAt(code.Count - 1);
        if (code.Count == 0) return "The query is empty.";
        if (!(code[0].Is("SELECT") || code[0].Is("WITH") || code[0].IsP("("))) return "A compared query must be a SELECT (or WITH … SELECT).";
        // Top-level words only: FOR UPDATE locks rows, WITH FUNCTION declares PL/SQL, a WITH must end in a SELECT.
        var depth = 0;
        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].IsP("(")) depth++;
            else if (code[i].IsP(")")) depth--;
            if (depth != 0) continue;
            if (code[i].Is("FOR") && i + 1 < code.Count && code[i + 1].Is("UPDATE")) return "A compared query cannot lock rows (FOR UPDATE).";
            if (i == 0 && code[0].Is("WITH") && code.Count > 1 && (code[1].Is("FUNCTION") || code[1].Is("PROCEDURE"))) return "A compared query cannot declare PL/SQL (WITH FUNCTION).";
        }
        if (code[0].Is("WITH"))
        {
            depth = 0;
            var verb = "";
            foreach (var t in code.Skip(1))
            {
                if (t.IsP("(")) depth++;
                else if (t.IsP(")")) depth--;
                else if (depth == 0 && t.Kind == OracleTokenKind.Word && t.Text.ToUpperInvariant() is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE") { verb = t.Text.ToUpperInvariant(); break; }
            }
            if (verb != "SELECT") return "A compared query must be a SELECT (or WITH … SELECT).";
        }
        return null;
    }

    /// <summary>Oracle takes WITH and ORDER BY inside an inline view: only the trailing semicolon (or <c>/</c>) and comments go.</summary>
    public (string Prefix, string Body) DerivedQuery(string sql)
    {
        var code = Code(sql);
        while (code.Count > 0 && (code[^1].IsP(";") || code[^1].IsP("/"))) code.RemoveAt(code.Count - 1);
        if (code.Count == 0) return ("", sql);
        return ("", sql[code[0].Offset..code[^1].End]);
    }

    public string IsolationHint =>
        "Rows that change while the compare reads show up as differences: compare a quiet copy, or read both sides AS OF one SCN or TIMESTAMP in custom queries (Flashback Query).";

    /// <summary>GENERATED BY DEFAULT identity columns take the copied values; a GENERATED ALWAYS one refuses them, and the server says so.</summary>
    public (string On, string Off)? IdentityInsert(string table) => null;
    public string? InsertOverriding => null;

    /// <summary>Oracle 19c has no multi-row VALUES: one INSERT per row.</summary>
    public int InsertBatch => 1;

    /// <summary>One anonymous block: a failed statement undoes the whole block (Oracle rolls back the call), so nothing is half-applied.</summary>
    public (string Begin, string Commit) SyncTransaction => (
        "-- One PL/SQL block: if a statement fails, Oracle undoes the whole block. Outside a transaction it commits when the block ends;\n" +
        "-- inside your open transaction it becomes part of it (COMMIT or ROLLBACK it yourself).\nBEGIN",
        "END;\n/");
}
