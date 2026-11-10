namespace Querithm.Intelligence.Oracle;

public enum OracleStatementKind { Sql, PlSql, ClientCommand }

/// <summary>
/// A SQL*Plus command line (D4 S02, O5): never sent to the server. <c>SET SERVEROUTPUT ON|OFF</c> sets
/// <see cref="ServerOutput"/> (the DBMS_OUTPUT toggle D4 S04 applies); every other command carries a <see cref="Refusal"/>.
/// </summary>
public sealed record OracleClientCommand(string Name, string Text, int Line, bool? ServerOutput, string? Refusal);

/// <summary>
/// One statement: its tokens (trivia inside it included), and where the text sent to the driver starts and ends. A plain
/// SQL statement's <c>;</c> and every <c>/</c> terminator line are outside [Start, End); a PL/SQL unit keeps its final
/// <c>END;</c>.
/// </summary>
public sealed record OracleStatement(OracleStatementKind Kind, List<OracleToken> Tokens, int Start, int End, int StartLine, int EndLine, OracleClientCommand? Command = null)
{
    public IEnumerable<OracleToken> Code => Tokens.Where(t => !t.IsTrivia);
}

/// <summary>
/// Statement boundaries as SQL*Plus draws them (D4 S02), on <see cref="OracleTokenizer"/> tokens, so a <c>;</c> or
/// <c>/</c> inside a string, a quoted name or a comment never ends anything:
/// <list type="bullet">
/// <item>Plain SQL ends at <c>;</c> or at a <c>/</c> alone on its line; the terminator is not part of the statement.</item>
/// <item>PL/SQL units — <c>DECLARE</c> / <c>BEGIN</c> / <c>&lt;&lt;label&gt;&gt;</c> blocks, <c>CREATE [OR REPLACE]
/// [EDITIONABLE | NONEDITIONABLE]</c> <c>PROCEDURE</c>, <c>FUNCTION</c>, <c>PACKAGE [BODY]</c>, <c>TRIGGER</c> (compound
/// included), <c>TYPE BODY</c>, <c>LIBRARY</c>, <c>JAVA</c>, and <c>WITH FUNCTION</c> queries — end only at a <c>/</c>
/// line or at the end of the script, and keep their <c>;</c>.</item>
/// <item><c>CREATE TYPE</c> without <c>BODY</c> (an object spec, methods declared, a collection, a forward declaration)
/// holds no PL/SQL scope: it ends at its <c>;</c> like SQL, or at a <c>/</c> line.</item>
/// <item>A SQL*Plus command (first word of a line before any statement text: <c>SET</c> but not <c>SET TRANSACTION</c> /
/// <c>ROLE</c> / <c>CONSTRAINT[S]</c>, <c>PROMPT</c>, <c>SPOOL</c>, <c>EXEC</c>, <c>DEFINE</c>, <c>@file</c> …) runs to the
/// end of its line (a trailing <c>-</c> continues it) and becomes an <see cref="OracleClientCommand"/>.</item>
/// </list>
/// </summary>
public static class OracleSplitter
{
    static readonly HashSet<string> ClientWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCEPT", "ACC", "APPEND", "ARCHIVE", "ATTRIBUTE", "BREAK", "BRE", "BTITLE", "CLEAR", "CL", "COLUMN", "COL", "COMPUTE", "COMP",
        "CONNECT", "CONN", "COPY", "DEFINE", "DEF", "DESCRIBE", "DESC", "DISCONNECT", "DISC", "EDIT", "ED", "EXECUTE", "EXEC", "EXIT",
        "QUIT", "GET", "HELP", "HOST", "HO", "INPUT", "LIST", "PASSWORD", "PAUSE", "PRINT", "PROMPT", "PRO", "RECOVER", "REMARK", "REM",
        "REPFOOTER", "REPHEADER", "RUN", "SAVE", "SET", "SHOW", "SHO", "SHUTDOWN", "SPOOL", "SPO", "START", "STA", "STARTUP", "STORE",
        "TIMING", "TTITLE", "UNDEFINE", "VARIABLE", "VAR", "WHENEVER", "XQUERY",
    };

    /// <summary><c>SET x</c> forms that are SQL, not SQL*Plus.</summary>
    static readonly HashSet<string> SqlSetWords = new(StringComparer.OrdinalIgnoreCase) { "TRANSACTION", "ROLE", "CONSTRAINT", "CONSTRAINTS" };

    static readonly HashSet<string> PlSqlUnits = new(StringComparer.OrdinalIgnoreCase) { "PROCEDURE", "FUNCTION", "PACKAGE", "TRIGGER", "LIBRARY", "JAVA" };

    public static List<OracleStatement> Split(string text)
    {
        text ??= "";
        var tokens = OracleTokenizer.Tokenize(text);
        var result = new List<OracleStatement>();
        var current = new List<OracleToken>();
        var code = new List<OracleToken>();
        bool? plsql = null;

        void Close()
        {
            if (code.Count > 0)
            {
                var kind = (plsql ??= IsPlSql(code)) ? OracleStatementKind.PlSql : OracleStatementKind.Sql;
                // Trailing trivia is not part of the statement.
                var last = current.FindLastIndex(t => !t.IsTrivia);
                var kept = current.GetRange(0, last + 1);
                result.Add(new OracleStatement(kind, kept, code[0].Offset, code[^1].End, code[0].Line, code[^1].Line + LinesIn(code[^1].Text)));
            }
            current = [];
            code = [];
            plsql = null;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.IsP("/") && AloneOnLine(text, t))
            {
                Close();
                continue;
            }
            if (code.Count == 0)
            {
                if (t.IsTrivia) continue;
                if (StartsLine(text, t.Offset) && IsClientStart(tokens, i))
                {
                    var (command, next) = ClientCommand(text, tokens, i);
                    result.Add(command);
                    i = next - 1;
                    continue;
                }
            }
            current.Add(t);
            if (t.IsTrivia) continue;
            if (t.IsP(";"))
            {
                plsql ??= IsPlSql(code);
                if (!plsql.Value) { current.RemoveAt(current.Count - 1); Close(); continue; }
            }
            code.Add(t);
        }
        Close();
        return result;
    }

    /// <summary>The unit kind from its first words: anonymous blocks, stored PL/SQL, <c>TYPE BODY</c>, <c>WITH FUNCTION</c>.</summary>
    public static bool IsPlSql(IReadOnlyList<OracleToken> code)
    {
        if (code.Count == 0) return false;
        if (code[0].Is("DECLARE") || code[0].Is("BEGIN") || code[0].IsP("<<")) return true;
        if (code[0].Is("WITH")) return code.Count > 1 && (code[1].Is("FUNCTION") || code[1].Is("PROCEDURE"));
        if (!code[0].Is("CREATE")) return false;
        var i = 1;
        if (i + 1 < code.Count && code[i].Is("OR") && code[i + 1].Is("REPLACE")) i += 2;
        while (i < code.Count && (code[i].Is("EDITIONABLE") || code[i].Is("NONEDITIONABLE") || code[i].Is("EDITIONING")
            || code[i].Is("FORCE") || code[i].Is("NOFORCE") || code[i].Is("SHARING") || code[i].Is("AND") || code[i].Is("RESOLVE")
            || code[i].Is("COMPILE") || code[i].Is("NO") || code[i].Is("OR")))
            i++;
        if (i >= code.Count) return false;
        if (code[i].Is("TYPE")) return i + 1 < code.Count && code[i + 1].Is("BODY");
        return PlSqlUnits.Contains(code[i].Text);
    }

    static bool IsClientStart(List<OracleToken> tokens, int i)
    {
        var t = tokens[i];
        if (t.Kind == OracleTokenKind.Operator && t.Text[0] is '@' or '!') return true;
        if (t.Kind != OracleTokenKind.Word || !ClientWords.Contains(t.Text)) return false;
        var next = NextCode(tokens, i);
        if (t.Is("SET")) return next is not { } n || !(n.Kind == OracleTokenKind.Word && SqlSetWords.Contains(n.Text));
        return true;
    }

    static OracleToken? NextCode(List<OracleToken> tokens, int i)
    {
        for (var k = i + 1; k < tokens.Count; k++) if (!tokens[k].IsTrivia) return tokens[k];
        return null;
    }

    /// <summary>The command from token <paramref name="i"/> to the end of its line (a trailing <c>-</c> continues it).</summary>
    static (OracleStatement Command, int Next) ClientCommand(string text, List<OracleToken> tokens, int i)
    {
        var start = tokens[i].Offset;
        var end = start;
        while (true)
        {
            var nl = text.IndexOf('\n', end);
            var lineEnd = nl < 0 ? text.Length : nl;
            var trimmed = text[start..lineEnd].TrimEnd();
            end = lineEnd;
            if (trimmed.EndsWith('-') && nl >= 0) { end = nl + 1; continue; }
            break;
        }
        var k = i;
        var own = new List<OracleToken>();
        while (k < tokens.Count && tokens[k].Offset < end) { own.Add(tokens[k]); k++; }
        var raw = text[start..end].TrimEnd();
        if (raw.EndsWith(';')) raw = raw[..^1].TrimEnd();
        var words = own.Where(t => !t.IsTrivia && !t.IsP(";")).ToList();
        var line = tokens[i].Line;
        var name = words[0].Kind == OracleTokenKind.Word ? words[0].Text.ToUpperInvariant() : words[0].Text[..1] is "@" ? "@" : words[0].Text[..1];
        bool? serverOutput = null;
        string? refusal = null;
        if (words[0].Is("SET") && words.Count >= 3 && words[1].Kind == OracleTokenKind.Word && IsServerOutput(words[1].Text)
            && (words[2].Is("ON") || words[2].Is("OFF")))
            serverOutput = words[2].Is("ON");
        else
            refusal = Refusal(name, line);
        var cmd = new OracleClientCommand(name, raw, line, serverOutput, refusal);
        var endLine = line + LinesIn(text[start..end].TrimEnd());
        return (new OracleStatement(OracleStatementKind.ClientCommand, own, start, start + raw.Length, line, endLine, cmd), k);
    }

    /// <summary>SERVEROUTPUT and its SQL*Plus abbreviations down to SERVEROUT.</summary>
    static bool IsServerOutput(string word) =>
        word.Length is >= 9 and <= 12 && "SERVEROUTPUT".StartsWith(word, StringComparison.OrdinalIgnoreCase);

    static string Refusal(string name, int line) => name switch
    {
        "EXEC" or "EXECUTE" => $"EXEC on line {line} is a SQL*Plus command, which Querithm does not run. Write the call as a block instead: BEGIN proc(…); END;",
        "@" => $"\"@file\" on line {line} runs a SQL*Plus script file, which Querithm does not do. Open the file and run its text instead.",
        "CONNECT" or "CONN" or "DISCONNECT" or "DISC" => $"{name} on line {line} is a SQL*Plus command, which Querithm does not run. Use a connection profile to sign in as another user.",
        "DESCRIBE" or "DESC" => $"{name} on line {line} is a SQL*Plus command, which Querithm does not run. The Explorer shows an object's columns.",
        _ => $"{name} on line {line} is a SQL*Plus command, which Querithm does not run. Only SET SERVEROUTPUT ON / OFF is understood; remove the line and run the script again.",
    };

    /// <summary>A <c>/</c> with only spaces and tabs between it and both ends of its line.</summary>
    static bool AloneOnLine(string text, OracleToken t) => StartsLine(text, t.Offset) && EndsLine(text, t.End);

    static bool StartsLine(string text, int offset)
    {
        for (var i = offset - 1; i >= 0; i--)
        {
            if (text[i] == '\n') return true;
            if (text[i] is not (' ' or '\t' or '\r')) return false;
        }
        return true;
    }

    static bool EndsLine(string text, int offset)
    {
        for (var i = offset; i < text.Length; i++)
        {
            if (text[i] == '\n') return true;
            if (text[i] is not (' ' or '\t' or '\r')) return false;
        }
        return true;
    }

    static int LinesIn(string s)
    {
        var n = 0;
        foreach (var c in s) if (c == '\n') n++;
        return n;
    }
}
