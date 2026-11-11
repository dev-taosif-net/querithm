using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Compare;
using Querithm.Providers.Oracle;

namespace Querithm.Tests;

/// <summary>
/// D4 S11 data compare on Oracle: kinds of every catalog type, keys ordered in binary text order in the ORDER BY itself,
/// LOB-like values refused with the reason, custom queries (one SELECT / WITH … SELECT, no FOR UPDATE, no PL/SQL), the
/// sync block; Core's exact numbers (38-digit keys apart, 1.50 = 1.5) and '' = NULL when either side is Oracle.
/// </summary>
public sealed class OracleDataCompareTests
{
    static readonly OracleDataCompare D = new();

    [Theory]
    [InlineData("NUMBER(10)", ValueKind.Integer)]
    [InlineData("NUMBER(38)", ValueKind.Integer)]
    [InlineData("INTEGER", ValueKind.Integer)]
    [InlineData("NUMBER(12,2)", ValueKind.Decimal)]
    [InlineData("NUMBER", ValueKind.Decimal)]
    [InlineData("FLOAT(126)", ValueKind.Decimal)]
    [InlineData("BINARY_DOUBLE", ValueKind.Float)]
    [InlineData("DATE", ValueKind.DateTime)]
    [InlineData("TIMESTAMP(9)", ValueKind.DateTime)]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", ValueKind.DateTime)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", ValueKind.DateTimeOffset)]
    [InlineData("CHAR(3 BYTE)", ValueKind.PaddedText)]
    [InlineData("NCHAR(3)", ValueKind.PaddedText)]
    [InlineData("VARCHAR2(40 CHAR)", ValueKind.Text)]
    [InlineData("NVARCHAR2(40)", ValueKind.Text)]
    [InlineData("RAW(16)", ValueKind.Binary)]
    [InlineData("BOOLEAN", ValueKind.Bool)]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", ValueKind.Unordered)]
    [InlineData("ROWID", ValueKind.Unordered)]
    [InlineData("CLOB", ValueKind.Unordered)]
    [InlineData("APP.ADDRESS_T", ValueKind.Unordered)]
    public void Kinds(string type, ValueKind kind) => Assert.Equal(kind, D.KindOf(type));

    [Fact]
    public void Text_keys_order_by_their_binary_code_whatever_the_sessions_NLS_SORT()
    {
        Assert.Equal("NLSSORT(\"NAME\", 'NLS_SORT=BINARY')", D.OrderExpression("\"NAME\"", ValueKind.Text));
        Assert.Equal("NLSSORT(q.\"C\", 'NLS_SORT=BINARY')", D.OrderExpression("q.\"C\"", ValueKind.PaddedText));
        Assert.Equal("\"ID\"", D.OrderExpression("\"ID\"", ValueKind.Integer));
        Assert.False(D.PadsTrailingSpaces);
        Assert.True(D.EmptyTextIsNull);
        Assert.True(D.QueryTypesFromProvider);
    }

    [Theory]
    [InlineData("CLOB", "read in part")]
    [InlineData("NCLOB", "read in part")]
    [InlineData("BLOB", "read in part")]
    [InlineData("LONG", "cannot be read")]
    [InlineData("BFILE", "points to a file")]
    [InlineData("XMLTYPE", "not compared")]
    [InlineData("JSON", "not compared")]
    [InlineData("APP.ADDRESS_T", "object or collection type")]
    public void Values_the_compare_cannot_read_whole_are_refused_with_the_reason(string type, string reason) => Assert.Contains(reason, D.ValueRefusal(type));

    [Theory]
    [InlineData("VARCHAR2(10)")]
    [InlineData("NUMBER")]
    [InlineData("RAW(2000)")]
    [InlineData("INTERVAL YEAR(2) TO MONTH")]
    public void Ordinary_values_are_compared(string type) => Assert.Null(D.ValueRefusal(type));

    [Theory]
    [InlineData("SELECT id FROM t", null)]
    [InlineData("SELECT id FROM t;", null)]
    [InlineData("  with a as (select 1 x from dual) select * from a order by x", null)]
    [InlineData("(SELECT 1 FROM dual) UNION ALL (SELECT 2 FROM dual)", null)]
    [InlineData("SELECT * FROM t WHERE c = 'FOR UPDATE'", null)]
    [InlineData("SELECT * FROM t FOR UPDATE", "FOR UPDATE")]
    [InlineData("SELECT * FROM t FOR UPDATE OF c NOWAIT", "FOR UPDATE")]
    [InlineData("WITH FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; SELECT f FROM dual", "WITH FUNCTION")]
    [InlineData("WITH a AS (SELECT 1 x FROM dual) DELETE FROM t", "must be a SELECT")]
    [InlineData("DELETE FROM t", "must be a SELECT")]
    [InlineData("BEGIN NULL; END;", "PL/SQL block")]
    [InlineData("SET SERVEROUTPUT ON", "SQL*Plus")]
    [InlineData("SELECT 1 FROM dual; SELECT 2 FROM dual", "exactly one statement")]
    [InlineData("  -- nothing\n", "empty")]
    public void A_custom_query_must_be_one_select(string sql, string? refusal)
    {
        var why = D.QueryRefusal(sql);
        if (refusal == null) Assert.Null(why);
        else Assert.Contains(refusal, why);
    }

    [Theory]
    [InlineData("SELECT id FROM t ORDER BY id;  -- trailing", "SELECT id FROM t ORDER BY id")]
    [InlineData("WITH a AS (SELECT 1 x FROM dual) SELECT x FROM a\n/", "WITH a AS (SELECT 1 x FROM dual) SELECT x FROM a")]
    public void A_custom_query_keeps_WITH_and_ORDER_BY_inside_the_inline_view(string sql, string body) => Assert.Equal(("", body), D.DerivedQuery(sql));

    [Fact]
    public void The_sync_script_is_one_PL_SQL_block_one_insert_per_row()
    {
        var (begin, commit) = D.SyncTransaction;
        Assert.EndsWith("\nBEGIN", begin);
        Assert.All(begin.Split('\n').SkipLast(1), l => Assert.StartsWith("--", l));
        Assert.Equal("END;\n/", commit);
        Assert.Equal(1, D.InsertBatch);
        Assert.Null(D.IdentityInsert("\"APP\".\"T\""));
        Assert.Null(D.InsertOverriding);
    }

    // ------------------------------------------------------------------------------------------ Core: exact numbers, '' = NULL

    [Theory]
    [InlineData("12345678901234567890123456789012345671", "12345678901234567890123456789012345672", -1)]
    [InlineData("1.50", "1.5", 0)]
    [InlineData("-0", "0", 0)]
    [InlineData("1e3", "1000", 0)]
    [InlineData("-2.5", "-2.49999999999999999999999999999999", -1)]
    [InlineData("0.000000000000000000000000000000001", "0", 1)]
    [InlineData("99999999999999999999999999999999999999", "1E38", -1)]
    [InlineData(" 7 ", "7.0", 0)]
    public void Numbers_compare_exactly_past_28_digits(string a, string b, int sign)
    {
        Assert.True(ExactNumber.TryParse(a, out var x));
        Assert.True(ExactNumber.TryParse(b, out var y));
        Assert.Equal(sign, Math.Sign(x.CompareTo(y)));
        Assert.Equal(sign == 0, KeyComparers.ValuesEqual(ValueKind.Decimal, ValueKind.Decimal, a, b, new DataCompareOptions()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("1e")]
    [InlineData("NaN")]
    [InlineData("1,5")]
    [InlineData("--1")]
    public void Text_that_is_not_a_number_is_not_one(string text) => Assert.False(ExactNumber.TryParse(text, out _));

    [Fact]
    public async Task Keys_that_differ_in_their_38th_digit_are_two_rows_not_a_duplicate()
    {
        var plan = new ComparePlan(["ID"], [ValueKind.Integer], [ValueKind.Integer], ["V"], [ValueKind.Text], [ValueKind.Text], false, new DataCompareOptions());
        var counts = new CompareCounts();
        var diffs = new List<DiffRow>();
        string?[][] rows = [["12345678901234567890123456789012345671", "a"], ["12345678901234567890123456789012345672", "b"]];
        await DataComparer.MergeAsync(Rows(rows), Rows(rows), plan, counts, d => { diffs.Add(d); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, counts.Identical);
        Assert.Empty(diffs);
    }

    [Fact]
    public async Task An_empty_string_and_NULL_are_one_value_only_when_a_side_stores_them_so()
    {
        string?[][] source = [["1", ""], ["2", "x"]];
        string?[][] target = [["1", null], ["2", "x"]];
        async Task<long> Different(bool emptyIsNull)
        {
            var plan = new ComparePlan(["ID"], [ValueKind.Integer], [ValueKind.Integer], ["V"], [ValueKind.Text], [ValueKind.Text], false, new DataCompareOptions(), emptyIsNull);
            var counts = new CompareCounts();
            await DataComparer.MergeAsync(Rows(source), Rows(target), plan, counts, _ => ValueTask.CompletedTask, CancellationToken.None);
            return counts.Different;
        }
        Assert.Equal(0, await Different(emptyIsNull: true));
        Assert.Equal(1, await Different(emptyIsNull: false));
    }

    static async IAsyncEnumerator<string?[]> Rows(string?[][] rows)
    {
        foreach (var r in rows) { await Task.Yield(); yield return r; }
    }

    [Fact]
    public void Other_engines_keep_their_defaults()
    {
        IDataCompareDialect pg = new Querithm.Providers.Postgres.PostgresDataCompare();
        Assert.False(pg.EmptyTextIsNull);
        Assert.Null(pg.ValueRefusal("text"));
        Assert.False(pg.QueryTypesFromProvider);
    }

    [Fact]
    public void A_mixed_number_key_family_still_compares_exact_with_float()
    {
        var a = KeyComparers.Normalize(ValueKind.Decimal, "1.5", false, "k");
        var b = KeyComparers.Normalize(ValueKind.Float, "1.25", false, "k");
        Assert.True(KeyComparers.CompareKeys([a], [b]) > 0);
        Assert.Throws<QuerithmException>(() => KeyComparers.Normalize(ValueKind.Integer, "x", false, "k"));
    }
}
