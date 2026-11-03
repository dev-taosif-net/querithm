namespace Querithm.Core.Compare;

/// <summary>(schema, name) pairs compared the way the engine compares names (case-insensitive on SQL Server).</summary>
public sealed class NamePairComparer(StringComparer c) : IEqualityComparer<(string, string)>
{
    public bool Equals((string, string) x, (string, string) y) => c.Equals(x.Item1, y.Item1) && c.Equals(x.Item2, y.Item2);
    public int GetHashCode((string, string) o) => HashCode.Combine(c.GetHashCode(o.Item1), c.GetHashCode(o.Item2));
}
