namespace Querithm.Intelligence.Oracle;

/// <summary>
/// Oracle reserved words (D4 S02). Source: the server's own list,
/// <code>SELECT keyword FROM v$reserved_words WHERE reserved = 'Y' ORDER BY keyword</code>
/// (Oracle 19c / 23ai), cross-checked with the SQL Language Reference appendix "Oracle SQL Reserved Words". A bare
/// identifier must not be one of these, so <see cref="OracleIdentifiers.QuoteIfNeeded"/> quotes them.
/// Not yet regenerated from the Free fixture (no Docker in the S02 session): STATUS.md lists the check as open.
/// <see cref="PlSqlReserved"/> are the PL/SQL reserved words (PL/SQL Language Reference, appendix D) a PL/SQL unit may
/// not use bare either.
/// </summary>
public static class OracleKeywords
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCESS", "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUDIT", "BETWEEN", "BY", "CHAR", "CHECK", "CLUSTER", "COLUMN",
        "COMMENT", "COMPRESS", "CONNECT", "CREATE", "CURRENT", "DATE", "DECIMAL", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP",
        "ELSE", "EXCLUSIVE", "EXISTS", "FILE", "FLOAT", "FOR", "FROM", "GRANT", "GROUP", "HAVING", "IDENTIFIED", "IMMEDIATE", "IN",
        "INCREMENT", "INDEX", "INITIAL", "INSERT", "INTEGER", "INTERSECT", "INTO", "IS", "LEVEL", "LIKE", "LOCK", "LONG",
        "MAXEXTENTS", "MINUS", "MLSLABEL", "MODE", "MODIFY", "NOAUDIT", "NOCOMPRESS", "NOT", "NOWAIT", "NULL", "NUMBER", "OF",
        "OFFLINE", "ON", "ONLINE", "OPTION", "OR", "ORDER", "PCTFREE", "PRIOR", "PUBLIC", "RAW", "RENAME", "RESOURCE", "REVOKE",
        "ROW", "ROWID", "ROWNUM", "ROWS", "SELECT", "SESSION", "SET", "SHARE", "SIZE", "SMALLINT", "START", "SUCCESSFUL",
        "SYNONYM", "SYSDATE", "TABLE", "THEN", "TO", "TRIGGER", "UID", "UNION", "UNIQUE", "UPDATE", "USER", "VALIDATE", "VALUES",
        "VARCHAR", "VARCHAR2", "VIEW", "WHENEVER", "WHERE", "WITH",
    };

    static readonly HashSet<string> PlSql = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEGIN", "CASE", "CURSOR", "DECLARE", "END", "EXCEPTION", "FETCH", "FUNCTION", "GOTO", "LOOP", "OPEN", "PACKAGE",
        "PROCEDURE", "RAISE", "RETURN", "TYPE", "WHEN", "WHILE", "ELSIF", "EXIT", "IF", "OUT", "PRAGMA", "RECORD", "SUBTYPE",
    };

    public static bool IsReserved(string word) => Reserved.Contains(word) || PlSql.Contains(word);

    /// <summary>The reserved words, for highlighting and completion.</summary>
    public static IReadOnlyCollection<string> PlSqlReserved => PlSql;
    public static IReadOnlyCollection<string> SqlReserved => Reserved;
}
