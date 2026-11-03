using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Sqlite;

namespace Querithm.Providers.Sqlite;

/// <summary>
/// EXPLAIN QUERY PLAN (Phase D1): rows (id, parent, notused, detail) become a <see cref="PlanTree"/> by id/parent; the
/// detail text is kept as the operator, opaque (SQLite says its format may change between releases). No costs, no row
/// estimates, no actual plans (the engine has none). Raw is the rows as JSON, format "sqlite-eqp", which plan.parse reads.
/// </summary>
public sealed class SqlitePlans : IPlanProvider
{
    public const string Format = "sqlite-eqp";

    sealed record Row(int Id, int Parent, string Detail);

    public const string ActualRefusal = "SQLite has no actual plans: EXPLAIN QUERY PLAN shows the planner's choice only.";

    public string? Refusal(string sql, PlanMode mode) => mode == PlanMode.Actual ? ActualRefusal : null;

    public async Task<PlanResult> GetPlanAsync(DbConnection c, string sql, PlanMode mode, CancellationToken ct)
    {
        if (mode == PlanMode.Actual) throw new NotSupportedException(ActualRefusal);
        var statements = new List<(string Text, List<Row> Rows)>();
        foreach (var st in SqliteSplitter.Split(SqliteTokenizer.Tokenize(sql)))
        {
            var code = st.Where(t => !t.IsTrivia).ToList();
            if (code.Count == 0) continue;
            var last = code[^1].IsP(";") ? code[^2] : code[^1];
            var text = sql[code[0].Offset..(last.Offset + last.Length)];
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + text;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<Row>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(new Row(Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture), r.GetString(3)));
            statements.Add((text, rows));
        }
        var raw = JsonSerializer.Serialize(statements.Select(s => new Dictionary<string, object>
        {
            ["text"] = s.Text, ["rows"] = s.Rows.Select(x => new object[] { x.Id, x.Parent, x.Detail }).ToArray(),
        }).ToArray());
        return new PlanResult { Plan = Parse(raw), Raw = raw, Format = Format };
    }

    /// <summary>Reads the rows JSON back (also what plan.parse gets for SQLite); untrusted: shape-checked, nothing runs.</summary>
    public PlanTree Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
        var list = new List<PlanStatement>();
        foreach (var st in doc.RootElement.EnumerateArray())
        {
            var rows = st.GetProperty("rows").EnumerateArray()
                .Select(r => new Row(r[0].GetInt32(), r[1].GetInt32(), r[2].GetString() ?? "")).ToList();
            var root = new PlanNode
            {
                Id = "0", Operator = "QUERY PLAN", PhysicalOp = "QUERY PLAN", Warnings = [], Properties = [], Children = Children(rows, 0),
            };
            list.Add(new PlanStatement { Text = st.TryGetProperty("text", out var t) ? t.GetString() : null, Root = root, MissingIndexes = [], Warnings = [] });
        }
        return new PlanTree { Statements = list.ToArray() };
    }

    static PlanNode[] Children(List<Row> rows, int parent) => rows.Where(r => r.Parent == parent && r.Id != parent).Select(r => new PlanNode
    {
        Id = r.Id.ToString(CultureInfo.InvariantCulture), Operator = r.Detail, PhysicalOp = Operator(r.Detail), Warnings = [],
        Properties = new Dictionary<string, string> { ["detail"] = r.Detail }, Children = Children(rows, r.Id),
    }).ToArray();

    /// <summary>The leading words of the detail ("SCAN", "SEARCH", "USE TEMP B-TREE"), for the node's label.</summary>
    static string Operator(string detail)
    {
        var words = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "?" : words[0] is "USE" or "COMPOUND" or "CORRELATED" && words.Length > 2 ? string.Join(' ', words.Take(3)) : words[0];
    }
}
