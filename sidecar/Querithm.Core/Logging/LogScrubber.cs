using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Querithm.Core.Logging;

/// <summary>
/// §13 rule 1: replaces Password=..., pwd=..., Authorization: ... and any known secret with ***
/// before a message leaves the sidecar. Regex here runs over log text, not query text.
/// </summary>
public static partial class LogScrubber
{
    static readonly ConcurrentDictionary<string, byte> KnownSecrets = new();
    static readonly ConcurrentDictionary<string, byte> KnownPaths = new();

    [GeneratedRegex(@"(?i)\b(password|pwd)\s*=\s*(""[^""]*""|'[^']*'|\{[^}]*\}|[^;""'\s]+)")]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"(?i)\b(authorization)\s*:\s*(bearer\s+|basic\s+)?\S+")]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(?i)(""(?:secret|sshSecret|passphrase|password|accessToken|token)""\s*:\s*)""(?:[^""\\]|\\.)*""")]
    private static partial Regex JsonSecretField();

    [GeneratedRegex(@"(?i)\b(access\s*token)\s*=\s*[^;]+")]
    private static partial Regex AccessToken();

    /// <summary>Registers a secret value so any occurrence is masked (called when a secret enters the sidecar).</summary>
    public static void RegisterSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 3) KnownSecrets.TryAdd(secret, 0);
    }

    /// <summary>
    /// Registers a granted database file's path (Phase D0c): paths are user data, so the file and its folder are replaced
    /// by "&lt;file&gt;" / "&lt;folder&gt;" in logs and in driver errors shown or logged by the sidecar.
    /// </summary>
    public static void RegisterPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3) return;
        KnownPaths.TryAdd(path, 0);
    }

    /// <summary>
    /// What a log line says about a failure (M21): a server error by its type and code only — its message may quote a row
    /// value (SQL Server and MySQL name the duplicate key) — anything else by its message.
    /// </summary>
    public static string Describe(Exception ex) => ex is System.Data.Common.DbException db
        ? $"{db.GetType().Name} {db.SqlState ?? db.ErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        : ex.Message;

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = KeyValueSecret().Replace(text, m => $"{m.Groups[1].Value}=***");
        s = AuthorizationHeader().Replace(s, m => $"{m.Groups[1].Value}: ***");
        s = JsonSecretField().Replace(s, m => $"{m.Groups[1].Value}\"***\"");
        s = AccessToken().Replace(s, m => $"{m.Groups[1].Value}=***");
        foreach (var secret in KnownSecrets.Keys)
            if (s.Contains(secret, StringComparison.Ordinal)) s = s.Replace(secret, "***", StringComparison.Ordinal);
        foreach (var path in KnownPaths.Keys)
        {
            if (s.Contains(path, StringComparison.Ordinal)) s = s.Replace(path, "<file>", StringComparison.Ordinal);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (dir is { Length: > 1 } && s.Contains(dir, StringComparison.Ordinal)) s = s.Replace(dir, "<folder>", StringComparison.Ordinal);
        }
        return s;
    }
}
