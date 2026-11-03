using System.Xml;
using Querithm.Contract;
using Querithm.Core.Sessions;

namespace Querithm.Core.Metadata;

/// <summary>
/// <c>plan.parse</c>: a plan the user already holds as text (a result cell with SQL Server showplan XML or a PostgreSQL
/// EXPLAIN (FORMAT JSON) value) parsed into the plan view's tree. The text is untrusted (it comes from the webview and,
/// before that, from a server): it is size-capped, XML is read without DTDs or external entities and with a depth
/// limit (the plan walk recurses per operator), and anything the engine's parser cannot read fails as <c>invalid</c>.
/// Nothing is sent to a server.
/// </summary>
public static class PlanParsing
{
    /// <summary>Largest plan text accepted, in characters (10 MB, the cell viewer's cap).</summary>
    public const int MaxChars = 10 * 1024 * 1024;

    /// <summary>Deepest XML element nesting accepted (about 250 nested operators).</summary>
    public const int MaxXmlDepth = 512;

    public static PlanParseResult Parse(ProviderRegistry providers, PlanParseParams? p)
    {
        if (p == null) throw QuerithmException.Invalid("plan.parse needs the plan text and its engine.");
        if (!Enum.IsDefined(p.Engine)) throw QuerithmException.Invalid($"Unknown engine '{p.Engine}'.");
        if (string.IsNullOrWhiteSpace(p.Raw)) throw QuerithmException.Invalid("The plan text is empty.");
        if (p.Raw.Length > MaxChars) throw QuerithmException.Invalid($"The plan text is larger than {MaxChars / 1024 / 1024} MB, the most a plan can be opened from.");
        var provider = providers.Get(p.Engine);
        // Phase D0e: each engine's own plan format and its own untrusted-input checks; no engine falls back to another's parser.
        return p.Engine switch
        {
            Engine.Mssql => ParseShowplan(provider.Plans, p.Raw),
            Engine.Pg => ParseExplainJson(provider.Plans, p.Raw),
            Engine.Sqlite => ParseOrInvalid(provider.Plans, p.Raw, "sqlite-eqp"),
            Engine.MySql => ParseOrInvalid(provider.Plans, p.Raw, "mysql-json"),
            Engine.DuckDb => ParseOrInvalid(provider.Plans, p.Raw, "duckdb-json"),
            _ => throw QuerithmException.Invalid($"Plans of {ProfileRules.EngineName(p.Engine)} cannot be opened from text."),
        };
    }

    /// <summary>A plan format whose parser checks its own shape (JSON depth-limited): anything it cannot read is refused.</summary>
    static PlanParseResult ParseOrInvalid(Contract.Providers.IPlanProvider plans, string raw, string format)
    {
        try { return new PlanParseResult { Plan = plans.Parse(raw), Format = format }; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
        {
            throw QuerithmException.Invalid("This text is not a plan Querithm can read.");
        }
    }

    static PlanParseResult ParseShowplan(Contract.Providers.IPlanProvider plans, string raw)
    {
        try
        {
            CheckXml(raw);
            var tree = plans.Parse(raw);
            if (tree.Statements.Length == 0) throw QuerithmException.Invalid("The showplan holds no statement plan.");
            return new PlanParseResult { Plan = tree, Format = "xml" };
        }
        catch (Exception e) when (e is not QuerithmException and not OutOfMemoryException)
        {
            throw QuerithmException.Invalid($"Not a SQL Server showplan that can be read: {e.Message}");
        }
    }

    /// <summary>Well-formed, no DTD, root element ShowPlanXML, nesting within <see cref="MaxXmlDepth"/>.</summary>
    static void CheckXml(string raw)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(new StringReader(raw), settings);
        var root = true;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (root)
            {
                if (reader.LocalName != "ShowPlanXML") throw QuerithmException.Invalid($"Not a SQL Server showplan: the root element is <{Short(reader.LocalName)}>, not <ShowPlanXML>.");
                root = false;
            }
            if (reader.Depth > MaxXmlDepth) throw QuerithmException.Invalid($"The showplan nests deeper than {MaxXmlDepth} elements.");
        }
        if (root) throw QuerithmException.Invalid("Not a SQL Server showplan: the text has no root element.");
    }

    static PlanParseResult ParseExplainJson(Contract.Providers.IPlanProvider plans, string raw)
    {
        try
        {
            var tree = plans.Parse(raw);
            if (tree.Statements.Length == 0) throw QuerithmException.Invalid("The EXPLAIN output holds no plan.");
            return new PlanParseResult { Plan = tree, Format = "json" };
        }
        catch (Exception e) when (e is not QuerithmException and not OutOfMemoryException)
        {
            // Malformed JSON (or deeper than JsonDocument's 64 levels), EnumerateArray on a non-array, GetProperty("Plan")
            // on an item without it, a non-number where a number belongs.
            throw QuerithmException.Invalid($"Not a PostgreSQL EXPLAIN (FORMAT JSON) plan that can be read: {e.Message}");
        }
    }

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
