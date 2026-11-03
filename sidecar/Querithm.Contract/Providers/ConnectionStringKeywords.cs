using System.Data.Common;

namespace Querithm.Contract.Providers;

/// <summary>A connection string read with one driver's keyword rules: the populated builder plus which canonical keywords it named.</summary>
public sealed class ParsedKeywords<T>(T builder, HashSet<string> named, List<string> unknown) where T : DbConnectionStringBuilder
{
    public T Builder { get; } = builder;
    /// <summary>Canonical (driver-spelled) keywords present in the string, so synonyms such as Server/Data Source match once.</summary>
    public HashSet<string> Named { get; } = named;
    public List<string> Unknown { get; } = unknown;
    public bool Has(string canonical) => Named.Contains(canonical);
}

public static class ConnectionStringKeywords
{
    /// <summary>
    /// Splits <paramref name="connectionString"/> with ADO.NET quoting rules and applies each keyword to a driver
    /// builder. Unknown keywords and rejected values are collected rather than thrown, so the caller can report
    /// them. Throws ArgumentException when the string is malformed or names nothing the driver knows.
    /// </summary>
    public static ParsedKeywords<T> Apply<T>(string connectionString, Func<T> create) where T : DbConnectionStringBuilder
    {
        var raw = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var target = create();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (string key in raw.Keys)
        {
            var value = raw[key];
            if (!target.ContainsKey(key))
            {
                unknown.Add(AsTyped(connectionString, key));
                continue;
            }
            try
            {
                // A one-key builder echoes the keyword back in the driver's own spelling ("Key=value").
                var one = create();
                one[key] = value;
                var echoed = one.ConnectionString;
                var eq = echoed.IndexOf('=');
                target[key] = value;
                named.Add(eq > 0 ? echoed[..eq].Trim() : key);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException)
            {
                unknown.Add($"{AsTyped(connectionString, key)} (invalid value)");
            }
        }
        if (named.Count == 0) throw new ArgumentException("No connection settings were recognised.");
        return new ParsedKeywords<T>(target, named, unknown);
    }

    // The keyword builder lowercases keys; show the user's own spelling where it can be found.
    static string AsTyped(string connectionString, string key)
    {
        var at = connectionString.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? key : connectionString.Substring(at, key.Length);
    }
}

/// <summary>
/// M3: the editor's warning for a TLS setting a pasted connection string names that is weaker than the engine's default
/// (§13 rule 2). <paramref name="setting"/> is the keyword and value as the user would recognise them ("SslMode=Required").
/// </summary>
public static class WeakTls
{
    public static string NotEncrypted(string setting) =>
        $"{setting}: the connection is not encrypted, so the password and every result cross the network in clear text.";
    public static string Downgradable(string setting) =>
        $"{setting}: TLS is used only when the server offers it and its certificate is not checked, so anyone on the network path can turn it off.";
    public static string CertNotChecked(string setting) =>
        $"{setting}: the connection is encrypted, but the server's certificate is not checked, so it does not prove which server answered.";
    public static string HostNotChecked(string setting) =>
        $"{setting}: the certificate chain is checked, but not the server's host name.";
}
