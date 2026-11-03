using System.Globalization;
using System.Text.RegularExpressions;
using Querithm.Contract;

namespace Querithm.Core.Execution;

/// <summary>Parses SET STATISTICS IO/TIME server messages (not query text) into rows (§11 Query statistics).</summary>
public static partial class StatisticsParser
{
    [GeneratedRegex(@"^Table '(?<table>[^']+)'\. (?<rest>.*)$", RegexOptions.Singleline)]
    private static partial Regex IoLine();

    [GeneratedRegex(@"(?<name>[a-z\- ]+?) (?<value>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IoPair();

    [GeneratedRegex(@"CPU time = (?<cpu>\d+) ms,\s+elapsed time = (?<elapsed>\d+) ms", RegexOptions.IgnoreCase)]
    private static partial Regex TimeLine();

    public static StatementStats? Parse(string message, int batchIndex)
    {
        var text = message.Trim();
        var io = IoLine().Match(text);
        if (io.Success)
        {
            long? Get(string key)
            {
                foreach (Match m in IoPair().Matches(io.Groups["rest"].Value))
                    if (string.Equals(m.Groups["name"].Value.Trim(), key, StringComparison.OrdinalIgnoreCase))
                        return long.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture);
                return null;
            }
            return new StatementStats
            {
                BatchIndex = batchIndex, Kind = "io", Table = io.Groups["table"].Value,
                ScanCount = Get("Scan count"), LogicalReads = Get("logical reads"), PhysicalReads = Get("physical reads"),
                ReadAheadReads = Get("read-ahead reads"), LobLogicalReads = Get("lob logical reads"),
            };
        }
        var time = TimeLine().Match(text);
        if (time.Success)
        {
            var kind = text.Contains("parse and compile", StringComparison.OrdinalIgnoreCase) ? "compile" : "time";
            return new StatementStats
            {
                BatchIndex = batchIndex, Kind = kind,
                CpuMs = double.Parse(time.Groups["cpu"].Value, CultureInfo.InvariantCulture),
                ElapsedMs = double.Parse(time.Groups["elapsed"].Value, CultureInfo.InvariantCulture),
            };
        }
        return null;
    }
}
