using Querithm.Contract;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 2 / §6 with the analysis limits (review 2026-11-25): a SQL Server script too deep or too long for the
/// classifier to parse is never run unchecked on production — it needs the destructive confirmation — and the guard says
/// why. Before, it said "The script has syntax errors", which misdescribes a valid script.
/// </summary>
public sealed class AnalysisLimitTests
{
    static async Task<DestructiveFinding> BlockedOnProd(string sql)
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "Billing", Engine = Engine.Mssql, Host = "prod", Environment = EnvironmentKind.Prod });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = s.SessionId, Sql = sql, Options = new ExecuteOptions { ConfirmedProd = true } });
        Assert.Null(r.ExecutionId);
        Assert.Equal(GuardReason.Destructive, r.Blocked!.Reason);
        return Assert.Single(r.Blocked.Findings);
    }

    [Fact]
    public async Task A_script_too_deep_to_check_needs_the_destructive_confirmation_and_says_why()
    {
        var n = TSqlParsing.MaxNesting + 1;
        var f = await BlockedOnProd("SELECT " + new string('(', n) + "1" + new string(')', n) + ";\nDROP TABLE dbo.Orders;");
        Assert.Equal("unparsed", f.Rule);
        Assert.Contains("too deep to analyse", f.Description);
        Assert.DoesNotContain("syntax errors", f.Description);
    }

    [Fact]
    public async Task Nested_begin_blocks_past_the_limit_are_not_parsed_and_need_the_confirmation()
    {
        var n = TSqlParsing.MaxNesting + 1;
        var f = await BlockedOnProd(string.Concat(Enumerable.Repeat("BEGIN ", n)) + "DROP TABLE dbo.Orders; " + string.Concat(Enumerable.Repeat("END ", n)));
        Assert.Equal("unparsed", f.Rule);
        Assert.Contains("too deep to analyse", f.Description);
    }

    [Fact]
    public async Task Comments_nested_past_the_limit_are_not_lexed_and_need_the_confirmation()
    {
        // Review 2026-11-26: ScriptDom's lexer recursed once per nested comment and 5,000 ended the process; past the limit
        // the text is refused before lexing. Only the T-SQL classifier reports parse errors (the other engines classify on
        // their own scanners, which loop), so this guard path is SQL Server's.
        var n = TSqlParsing.MaxNesting + 1;
        var f = await BlockedOnProd("DROP TABLE dbo.Orders; " + string.Concat(Enumerable.Repeat("/*", n)) + string.Concat(Enumerable.Repeat("*/", n)));
        Assert.Equal("unparsed", f.Rule);
        Assert.Contains("nests comments", f.Description);
    }

    [Fact]
    public async Task A_syntax_error_names_its_line_and_message()
    {
        var f = await BlockedOnProd("SELECT 1;\nSELECT 1 FROM");
        Assert.Equal("unparsed", f.Rule);
        Assert.StartsWith("The script could not be checked for destructive statements. Line 2: ", f.Description);
    }
}
