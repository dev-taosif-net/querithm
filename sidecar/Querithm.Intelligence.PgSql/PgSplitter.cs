namespace Querithm.Intelligence.PgSql;

/// <summary>
/// Statement boundaries without ';' (§9): SQL Server runs <c>SELECT 1\nSELECT 2</c> as two statements, PostgreSQL rejects
/// it as one, so the sidecar splits before a depth-0 statement keyword that cannot continue the current statement.
/// Most PostgreSQL statement keywords are unreserved (a column may be called <c>comment</c> or <c>update</c>), so every
/// rule also checks the tokens that follow; when in doubt the text stays one statement and the server reports it.
/// </summary>
sealed class PgSplitter
{
    static HashSet<string> Set(string words) => new(words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    /// <summary>Reserved words (plus type/function-name keywords that cannot be a bare name): never an identifier.</summary>
    internal static readonly HashSet<string> Reserved = Set("""
        ALL ANALYSE ANALYZE AND ANY ARRAY AS ASC ASYMMETRIC BOTH CASE CAST CHECK COLLATE COLUMN CONSTRAINT CREATE
        CURRENT_CATALOG CURRENT_DATE CURRENT_ROLE CURRENT_TIME CURRENT_TIMESTAMP CURRENT_USER DEFAULT DEFERRABLE DESC
        DISTINCT DO ELSE END EXCEPT FALSE FETCH FOR FOREIGN FROM GRANT GROUP HAVING IN INITIALLY INTERSECT INTO LATERAL
        LEADING LIMIT LOCALTIME LOCALTIMESTAMP NOT NULL OFFSET ON ONLY OR ORDER PLACING PRIMARY REFERENCES RETURNING
        SELECT SESSION_USER SOME SYMMETRIC SYSTEM_USER TABLE THEN TO TRAILING TRUE UNION UNIQUE USER USING VARIADIC WHEN
        WHERE WINDOW WITH AUTHORIZATION BINARY COLLATION CONCURRENTLY CROSS CURRENT_SCHEMA FREEZE FULL ILIKE INNER IS
        ISNULL JOIN LEFT LIKE NATURAL NOTNULL OUTER OVERLAPS RIGHT SIMILAR TABLESAMPLE VERBOSE
        """);

    /// <summary>Reserved words that can end a statement (<c>... IS NULL</c>, <c>ORDER BY x DESC</c>).</summary>
    static readonly HashSet<string> Terminal = Set("""
        NULL TRUE FALSE END ONLY ASC DESC DEFAULT CURRENT_DATE CURRENT_TIME CURRENT_TIMESTAMP CURRENT_USER CURRENT_ROLE
        CURRENT_CATALOG CURRENT_SCHEMA SESSION_USER SYSTEM_USER USER LOCALTIME LOCALTIMESTAMP
        """);

    /// <summary>Unreserved words after which a statement keyword continues the statement (trigger events, FOR NO KEY UPDATE, ...).</summary>
    static readonly HashSet<string> Continuation = Set("AFTER BEFORE OF INSTEAD ALSO REVOKE KEY EXPLAIN VACUUM RELEASE ROWS ROW QUERY ATOMIC BY");

    static readonly HashSet<string> Objects = Set("""
        TABLE VIEW MATERIALIZED INDEX SEQUENCE FUNCTION PROCEDURE ROUTINE AGGREGATE SCHEMA TYPE DOMAIN TRIGGER EXTENSION
        DATABASE ROLE USER GROUP POLICY RULE PUBLICATION SUBSCRIPTION SERVER FOREIGN EVENT OPERATOR COLLATION CONVERSION
        TEXT STATISTICS TABLESPACE LANGUAGE CAST OWNED LARGE ACCESS TRANSFORM
        """);

    /// <summary>Words after <c>ALTER|DROP &lt;word&gt;</c> that make <c>&lt;word&gt;</c> a column (<c>ALTER TABLE t ALTER type TYPE text</c>).</summary>
    static readonly HashSet<string> ColumnActions = Set("SET DROP TYPE ADD RESET CASCADE RESTRICT OPTIONS STORAGE COMPRESSION ALTER");

    /// <summary>ALTER statements whose clauses contain DROP/ADD &lt;object&gt; (<c>ALTER PUBLICATION p DROP TABLE t</c>).</summary>
    static readonly HashSet<string> NestingAlters = Set("EXTENSION OPERATOR PUBLICATION SUBSCRIPTION GROUP");

    /// <summary>Statement kinds that never contain a SET/RESET clause, so a depth-0 SET starts a new statement.</summary>
    static readonly HashSet<string> SetFree = Set("""
        SELECT DELETE TRUNCATE DROP SET RESET SHOW BEGIN START COMMIT ROLLBACK ABORT GRANT REVOKE COMMENT VACUUM ANALYZE
        CALL COPY LISTEN UNLISTEN NOTIFY DECLARE CLOSE FETCH DEALLOCATE DISCARD REFRESH REINDEX CLUSTER LOCK SAVEPOINT RELEASE
        """);

    static readonly HashSet<string> Transaction = Set("WORK TRANSACTION ISOLATION READ DEFERRABLE NOT AND PREPARED TO");

    static readonly HashSet<string> CursorOptions = Set("BINARY INSENSITIVE ASENSITIVE NO SCROLL CURSOR");

    readonly List<PgToken> _t;
    bool _started;
    string _kind = "";
    string _verb = "";
    string _object = "";
    bool _schemaElements;
    bool _insertPending;
    bool _withPending;
    bool _explainPending;

    PgSplitter(List<PgToken> tokens) => _t = tokens;

    /// <summary>Statements split on ';' outside parentheses, strings and dollar bodies, and before an unambiguous statement keyword.</summary>
    public static List<List<PgToken>> Split(List<PgToken> tokens)
    {
        var result = new List<List<PgToken>>();
        var cur = new List<PgToken>();
        var depth = 0;
        var s = new PgSplitter(tokens);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == PgTokenKind.Punct && t.Text == "(") depth++;
            else if (t.Kind == PgTokenKind.Punct && t.Text == ")") depth = Math.Max(0, depth - 1);
            if (t.Kind == PgTokenKind.Punct && t.Text == ";" && depth == 0)
            {
                if (cur.Any(x => !x.IsTrivia)) result.Add(cur);
                cur = [];
                s.Reset();
                continue;
            }
            if (depth == 0 && t.Kind == PgTokenKind.Word)
            {
                if (s._started && s.Starts(i))
                {
                    result.Add(cur);
                    cur = [];
                    s.Reset();
                }
                s.Observe(i);
            }
            else if (!t.IsTrivia && !s._started) s._started = true; // a statement that opens with '(' or a meta-command
            cur.Add(t);
        }
        if (cur.Any(x => !x.IsTrivia)) result.Add(cur);
        return result;
    }

    void Reset()
    {
        _started = _schemaElements = _insertPending = _withPending = _explainPending = false;
        _kind = _verb = _object = "";
    }

    static string U(PgToken? t) => t is { Kind: PgTokenKind.Word } w ? w.Text.ToUpperInvariant() : "";

    PgToken? Near(int i, int step, int count)
    {
        for (var j = i + step; j >= 0 && j < _t.Count; j += step)
            if (!_t[j].IsTrivia && --count == 0) return _t[j];
        return null;
    }

    static bool NameIsh(PgToken? t) => t is { Kind: PgTokenKind.QuotedIdent } || (t is { Kind: PgTokenKind.Word } w && !Reserved.Contains(w.Text));

    static bool Ends(PgToken? t) => t is null || t is { Kind: PgTokenKind.Punct, Text: ";" };

    static bool Paren(PgToken? t) => t is { Kind: PgTokenKind.Punct, Text: "(" };

    /// <summary>Track the statement kind and the clauses that let a following SELECT/WITH continue it.</summary>
    void Observe(int i)
    {
        var u = U(_t[i]);
        if (!_started)
        {
            _started = true;
            _kind = u;
            switch (u)
            {
                case "INSERT": _insertPending = true; break;
                case "WITH": _withPending = true; break;
                case "EXPLAIN": _explainPending = true; break;
                case "CREATE" or "ALTER":
                    var n = 1;
                    if (U(Near(i, 1, 1)) == "OR") n = 3;
                    _object = U(Near(i, 1, n));
                    _schemaElements = u == "CREATE" && _object == "SCHEMA" && U(Near(i, 1, n + 1)) != "IF";
                    break;
            }
            return;
        }
        switch (u)
        {
            case "SELECT" or "VALUES" or "TABLE": _insertPending = false; break;
            case "WITH" when IsCte(i): _withPending = true; break;
        }
        if (u is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "VALUES" or "TABLE" or "EXECUTE" && (_withPending || _explainPending))
        {
            _withPending = _explainPending = false;
            if (_verb == "") _verb = u;
            if (u == "INSERT") _insertPending = true;
        }
    }

    bool IsCte(int i)
    {
        var n1 = Near(i, 1, 1);
        if (U(n1) == "RECURSIVE") return true;
        var n2 = Near(i, 1, 2);
        return NameIsh(n1) && (U(n2) == "AS" || Paren(n2));
    }

    string Effective => _kind is "WITH" or "EXPLAIN" ? _verb : _kind;

    bool Pending => _insertPending || _withPending || _explainPending;

    bool Starts(int i)
    {
        var u = U(_t[i]);
        var prev = Near(i, -1, 1);
        switch (prev?.Kind)
        {
            case null: return false;
            case PgTokenKind.Punct when prev.Value.Text is "," or "[": return false;
            case PgTokenKind.Operator when prev.Value.Text != "*": return false;
            case PgTokenKind.Word:
                var pw = prev.Value.Text;
                if (Continuation.Contains(pw) || (Reserved.Contains(pw) && !Terminal.Contains(pw))) return false;
                break;
        }
        var n1 = Near(i, 1, 1);
        var n2 = Near(i, 1, 2);
        var w1 = U(n1);
        switch (u)
        {
            case "SELECT": return !Pending;
            case "WITH": return IsCte(i) && !Pending && _kind is not ("CREATE" or "ALTER" or "GRANT" or "REVOKE");
            case "INSERT": return w1 == "INTO" && !Pending && Effective != "MERGE";
            case "DELETE": return w1 == "FROM" && !Pending && Effective != "MERGE";
            case "UPDATE": return (NameIsh(n1) || w1 == "ONLY") && w1 != "SET" && !Pending && Effective != "MERGE";
            case "MERGE": return w1 == "INTO" && !Pending;
            case "SET" or "RESET" or "SHOW": return SetFree.Contains(Effective) && (NameIsh(n1) || w1 == "ALL");
            case "CREATE": return !_schemaElements;
            case "ALTER" or "DROP":
                if (_kind == "ALTER" && NestingAlters.Contains(_object)) return false;
                if (w1 == "DEFAULT") return u == "ALTER" && U(n2) == "PRIVILEGES";
                if (w1 == "SYSTEM") return u == "ALTER" && U(n2) is "SET" or "RESET";
                return Objects.Contains(w1) && (NameIsh(n2) || Objects.Contains(U(n2)) || U(n2) == "CONCURRENTLY") && !ColumnActions.Contains(U(n2));
            case "GRANT" or "REVOKE":
                return !_schemaElements && _kind != "ALTER" && w1 != "OPTION" && (NameIsh(n1) || w1 is "ALL" or "SELECT" or "CREATE" or "REFERENCES" or "GRANT");
            case "TRUNCATE": return NameIsh(n1) || w1 is "TABLE" or "ONLY";
            case "COMMENT": return w1 == "ON";
            case "BEGIN" or "COMMIT" or "ROLLBACK" or "ABORT": return Ends(n1) || Transaction.Contains(w1) || IsStarterWord(w1);
            case "START": return w1 == "TRANSACTION";
            case "EXPLAIN": return Paren(n1) || w1 is "ANALYZE" or "ANALYSE" or "VERBOSE" or "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "WITH" or "VALUES" or "TABLE" or "EXECUTE" or "CREATE" or "DECLARE";
            case "VACUUM": return Ends(n1) || Paren(n1) || NameIsh(n1) || w1 is "FULL" or "FREEZE" or "VERBOSE" or "ANALYZE";
            case "ANALYZE": return _kind is not ("EXPLAIN" or "VACUUM") && (Ends(n1) || Paren(n1) || NameIsh(n1) || w1 == "VERBOSE");
            case "COPY": return NameIsh(n1) || Paren(n1);
            case "CALL" or "PREPARE" or "LISTEN" or "NOTIFY" or "SAVEPOINT" or "RELEASE": return NameIsh(n1);
            case "UNLISTEN": return NameIsh(n1) || n1 is { Kind: PgTokenKind.Operator, Text: "*" };
            case "DO": return n1 is { Kind: PgTokenKind.DollarString or PgTokenKind.String } || w1 == "LANGUAGE";
            case "EXECUTE": return NameIsh(n1) && w1 is not ("FUNCTION" or "PROCEDURE") && !Pending;
            case "DEALLOCATE" or "CLOSE": return NameIsh(n1) || w1 == "ALL";
            case "DECLARE": return NameIsh(n1) && CursorOptions.Contains(U(n2));
            case "FETCH": return !(w1 is "FIRST" or "NEXT" && U(n2) is not ("FROM" or "IN")) && n1 is not null;
            case "REFRESH": return w1 == "MATERIALIZED";
            case "REINDEX" or "CLUSTER": return _kind is not ("ALTER" or "CREATE") && (Ends(n1) || Paren(n1) || NameIsh(n1));
            case "LOCK": return NameIsh(n1) || w1 == "TABLE";
            case "DISCARD": return w1 is "ALL" or "PLANS" or "SEQUENCES" or "TEMP" or "TEMPORARY";
            default: return false;
        }
    }

    static bool IsStarterWord(string w) => w is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "WITH" or "CREATE" or "ALTER" or "DROP" or "SET";
}
