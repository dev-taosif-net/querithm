using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;

namespace Querithm.Providers.DuckDb;

/// <summary>
/// DuckDB plans (Phase D3): <c>EXPLAIN (FORMAT JSON)</c> per statement for the estimated plan — a tree of
/// {name, children, extra_info} — and <c>EXPLAIN (ANALYZE, FORMAT JSON)</c> for the actual one, which runs the statement
/// and reports operator_cardinality and operator_timing per operator. Core guards the text as it guards query.execute and
/// runs an actual plan inside a transaction it always rolls back; DuckDB has no savepoints, so inside the user's open
/// transaction Core refuses it with the reason. Raw is [{text, actual, plan}] as JSON, format "duckdb-json", which
/// plan.parse reads back.
/// </summary>
public sealed class DuckDbPlans : IPlanProvider
{
    public const string Format = "duckdb-json";
    const int MaxDepth = 256;

    public async Task<PlanResult> GetPlanAsync(DbConnection c, string sql, PlanMode mode, CancellationToken ct)
    {
        var actual = mode == PlanMode.Actual;
        var statements = new JsonArray();
        foreach (var st in DuckDbTokenizer.Split(DuckDbTokenizer.Tokenize(sql)))
        {
            var code = st.Where(t => !t.IsTrivia).ToList();
            if (code.Count > 0 && code[^1].Text == ";") code.RemoveAt(code.Count - 1);
            if (code.Count == 0) continue;
            var text = sql[code[0].Offset..(code[^1].Offset + code[^1].Length)];
            await using var cmd = c.CreateCommand();
            cmd.CommandText = (actual ? "EXPLAIN (ANALYZE, FORMAT JSON) " : "EXPLAIN (FORMAT JSON) ") + text;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            string? json = null;
            while (await r.ReadAsync(ct).ConfigureAwait(false)) json = r.GetString(1);
            if (json == null) continue;
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth });
            // DuckDB 1.5.6 cannot print some profiles as JSON (count(*) answered from a file table's statistics): it says only "error".
            if (node is JsonObject o && o.Count == 1 && o["result"]?.GetValue<string>() == "error")
                throw new NotSupportedException("DuckDB could not report the actual plan of this statement (a limitation of DuckDB 1.5.6 for some queries on database files). The estimated plan is available.");
            statements.Add((JsonNode)new JsonObject { ["text"] = text, ["actual"] = actual, ["plan"] = node });
        }
        var raw = statements.ToJsonString();
        return new PlanResult { Plan = Parse(raw), Raw = raw, Format = Format };
    }

    /// <summary>Reads the raw JSON back (also what plan.parse gets for DuckDB); untrusted: shape-checked, nothing runs.</summary>
    public PlanTree Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = MaxDepth });
        var list = new List<PlanStatement>();
        foreach (var st in doc.RootElement.EnumerateArray())
        {
            var actual = st.TryGetProperty("actual", out var a) && a.ValueKind == JsonValueKind.True;
            var plan = st.GetProperty("plan");
            var next = 0;
            // Estimated: an array of root operators. Actual: the query's profile, whose child is the EXPLAIN_ANALYZE wrapper.
            var roots = plan.ValueKind switch
            {
                JsonValueKind.Array => plan.EnumerateArray().ToList(),
                JsonValueKind.Object => Children(plan).SelectMany(ch => Name(ch) == "EXPLAIN_ANALYZE" ? Children(ch) : [ch]).ToList(),
                _ => [],
            };
            var nodes = roots.Select(n => Node(n, ref next)).ToArray();
            var root = nodes.Length == 1 ? nodes[0] : new PlanNode
            {
                Id = "root", Operator = "QUERY", PhysicalOp = "QUERY", Warnings = [], Properties = [], Children = nodes,
            };
            if (actual && plan.ValueKind == JsonValueKind.Object && Number(plan, "latency") is { } latency) root = root with { ElapsedMs = latency * 1000 };
            list.Add(new PlanStatement { Text = st.TryGetProperty("text", out var t) ? t.GetString() : null, Root = root, MissingIndexes = [], Warnings = [] });
        }
        return new PlanTree { Statements = list.ToArray() };
    }

    static IEnumerable<JsonElement> Children(JsonElement n) =>
        n.ValueKind == JsonValueKind.Object && n.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray() : [];

    static string Name(JsonElement n) =>
        n.TryGetProperty("operator_name", out var on) && on.ValueKind == JsonValueKind.String ? on.GetString() ?? "?"
        : n.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? "?" : "?";

    static double? Number(JsonElement n, string property) =>
        n.TryGetProperty(property, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        } : null;

    static PlanNode Node(JsonElement n, ref int next)
    {
        var id = (next++).ToString(CultureInfo.InvariantCulture);
        var name = Name(n);
        var props = new Dictionary<string, string>();
        string? table = null;
        double est = 0;
        if (n.TryGetProperty("extra_info", out var extra) && extra.ValueKind == JsonValueKind.Object)
            foreach (var p in extra.EnumerateObject())
            {
                var value = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Array => string.Join(", ", p.Value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())),
                    _ => p.Value.GetRawText(),
                };
                if (p.Name == "Estimated Cardinality") double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out est);
                else if (p.Name == "Table") table = value;
                props[p.Name] = value;
            }
        var children = new List<PlanNode>();
        foreach (var c in Children(n)) children.Add(Node(c, ref next));
        var rows = Number(n, "operator_cardinality");
        var timing = Number(n, "operator_timing");
        return new PlanNode
        {
            Id = id, Operator = name, PhysicalOp = name, ObjectName = table, EstRows = est, ActualRows = rows, ActualExecutions = rows == null ? null : 1,
            ElapsedMs = timing * 1000, Warnings = [], Properties = props, Children = children.ToArray(),
        };
    }
}
