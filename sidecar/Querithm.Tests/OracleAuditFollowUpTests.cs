using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Tests;

/// <summary>Oracle intelligence, code audit 2026-11-25 follow-up: SQL*Plus continuation lines (L7) and completion's held document (M13).</summary>
public class OracleAuditFollowUpTests
{
    /// <summary>
    /// A blank line after a command that ends in <c>-</c> ends it: the check read the text from the command's start, so the
    /// blank line still "ended in -" and the next statement became part of the command (never run, never checked).
    /// </summary>
    [Fact]
    public void A_blank_line_ends_a_continued_client_command()
    {
        var st = OracleSplitter.Split("PROMPT one -\n\nDELETE FROM t;\n");
        Assert.Equal(2, st.Count);
        Assert.Equal(OracleStatementKind.ClientCommand, st[0].Kind);
        Assert.Equal(OracleStatementKind.Sql, st[1].Kind);
        Assert.Contains(new OracleLanguageService().Classify("PROMPT one -\n\nDELETE FROM t;\n").Destructive, f => f.Rule == "deleteWithoutWhere");
    }

    [Fact]
    public void A_continued_client_command_still_takes_its_next_line()
    {
        var st = OracleSplitter.Split("PROMPT one -\ntwo\nSELECT 1 FROM dual;");
        Assert.Equal(2, st.Count);
        Assert.Contains("two", st[0].Command!.Text);
        // Many continuation lines: linear, not a copy of every earlier line per line.
        var many = "PROMPT x -\n" + string.Concat(Enumerable.Repeat("y -\n", 20_000)) + "z\nSELECT 1 FROM dual;";
        Assert.Equal(2, OracleSplitter.Split(many).Count);
    }

    /// <summary>The thread-static copy of the last document is let go once the request ends (it held up to 16 MB per pool thread).</summary>
    [Fact]
    public void A_completion_request_does_not_keep_its_document_on_the_thread()
    {
        var snap = new SchemaSnapshot
        {
            ProfileId = "p", Database = "shop", Status = SnapshotStatus.Fresh, Fks = [],
            Objects = [new SnapObject { Id = 1, Schema = "APP", Name = "ORDERS", Kind = ObjectKind.Table, Columns = [new SnapColumn { Name = "ID", Type = "NUMBER" }] }],
        };
        var text = "ALTER SESSION SET CURRENT_SCHEMA = APP;\nSELECT  FROM orders";
        var items = new OracleLanguageService().Complete(new CompleteParams
        {
            DocId = "m13-" + Guid.NewGuid(), Text = text, Offset = text.IndexOf(" FROM", StringComparison.Ordinal), Dialect = Dialect.Oracle, ProfileId = "p", Database = "shop", Schema = "APP",
        }, new FakeSchema(snap, "shop"), CancellationToken.None);
        Assert.Contains(items, i => i.Label == "ID");
        Assert.False(OracleCompletion.HoldsDocument);
    }
}
