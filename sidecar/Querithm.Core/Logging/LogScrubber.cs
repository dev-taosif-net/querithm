using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Querithm.Core.Logging;

/// <summary>
/// §13 rule 1: replaces any known secret, Password=..., pwd=..., passphrase: ..., token=..., api_key: ..., --password x,
/// scheme://user:password@, Authorization: ... and JSON secret fields with *** before a message leaves the sidecar. Regex
/// here runs over log text, not query text.
/// </summary>
public static partial class LogScrubber
{
    static readonly ConcurrentDictionary<string, byte> KnownSecrets = new();
    static readonly ConcurrentDictionary<string, byte> KnownPaths = new();

    /// <summary>
    /// A registered secret shorter than this is not masked on its own (PT6-7, accepted): "q7" or "ab" would turn every
    /// such word in every line into ***. A short password is still masked in a key=value pair.
    /// </summary>
    public const int MinRegisteredLength = 3;

    // A key that ends in password / passphrase / pwd / secret / token / api key (SslPassword, SSH_PASSPHRASE, AccessToken,
    // x-api-key, ...) with "=" or ":". Quoted and braced values follow the connection-string rules: a doubled quote or "}}"
    // stays in the value; an unclosed one is masked up to the next ";". An unquoted value after "=" runs, spaces and all,
    // to the next ";" on the line (PT6-2: ADO.NET keeps inner spaces, "Password=a b c;Port=1"). With no ";" after it, or
    // after ":" ("Passphrase: x and more", "Password=x failed"), it ends at an ASCII space, so the rest of the log line is
    // kept; a no-break space (U+00A0) does not end it.
    // MySQL's "(using password: YES)" says whether a password was sent, not what it was, so it is kept (group "keep"):
    // only with "(using " before the key, ":" as the separator and the ")" ending the word (PT6-3).
    [GeneratedRegex(@"(?i)\b(?<key>[a-z_\-]*?(?:password|passphrase|pwd|secret|token|api[_\-]?key))\s*(?<sep>(?<eq>=)|:)\s*(?<value>(?<keep>(?<=\(using\s+password\s*:\s*)(?:yes|no)\)(?![^\s.,;]))|""(?:[^""]|"""")*""|'(?:[^']|'')*'|\{(?:[^}]|\}\})*\}|[""'{][^;]*|(?(eq)[^;\r\n]+(?=;)|(?!))|[^;""' \t\r\n\f\v]+)")]
    private static partial Regex KeyValueSecret();

    // scheme://user:password@host (postgresql://, mysql://, ssh://, ...): the password part, from the userinfo colon to the
    // token's last "@" (PT6-4: a raw "/" or "@" in the password). Not when a port and a path follow the colon
    // (https://h:8443/path@x has no userinfo).
    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.\-]*://[^:/@\s]*):(?!\d+/)(\S*)@")]
    private static partial Regex UriUserInfo();

    [GeneratedRegex(@"(?i)\b(authorization)\s*:\s*(bearer\s+|basic\s+)?\S+")]
    private static partial Regex AuthorizationHeader();

    const string JsonKeys = "secret|sshSecret|passphrase|sshPassphrase|password|pwd|accessToken|access_token|refreshToken|refresh_token|id_token|client_secret|apiKey|api_key|token";

    [GeneratedRegex(@"(?i)(""(?:" + JsonKeys + @")""\s*:\s*)""(?:[^""\\]|\\.)*""")]
    private static partial Regex JsonSecretField();

    // A secret field holding an array (PT6-5): {"password":["x"]}.
    [GeneratedRegex(@"(?i)(""(?:" + JsonKeys + @")""\s*:\s*)\[[^\]]*\]")]
    private static partial Regex JsonSecretArray();

    // JSON inside a JSON string (PT6-5): \"password\":\"x\" (any depth of backslashes).
    [GeneratedRegex(@"(?i)(\\+""(?:" + JsonKeys + @")\\+""\s*:\s*)\\+""(?:[^""\\]|\\(?!\\*""))*\\+""")]
    private static partial Regex EscapedJsonSecretField();

    // Command-line arguments (PT6-5): ["--password","x"] and --password x.
    [GeneratedRegex(@"(?i)(""--?[a-z0-9_\-]*?(?:password|passphrase|pwd|secret|token|api[_\-]?key)""\s*,\s*"")(?:[^""\\]|\\.)*""")]
    private static partial Regex ArgvJson();

    [GeneratedRegex(@"(?i)((?<![\w\-])--?[a-z0-9_\-]*?(?:password|passphrase|pwd|secret|token|api[_\-]?key)[ \t]+)(?!-)\S+")]
    private static partial Regex ArgvBare();

    [GeneratedRegex(@"(?i)\b(access\s*token)\s*=\s*[^;]+")]
    private static partial Regex AccessToken();

    /// <summary>Registers a secret value so any occurrence is masked (called when a secret enters the sidecar).</summary>
    public static void RegisterSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < MinRegisteredLength) return;
        KnownSecrets.TryAdd(secret, 0);
        // Its URL and base64 encodings too (PT6-6): a driver or a URI may echo the secret encoded.
        var escaped = Uri.EscapeDataString(secret);
        if (escaped != secret) KnownSecrets.TryAdd(escaped, 0);
        KnownSecrets.TryAdd(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), 0);
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
        // Known secrets first (PT6-1), longest first: a pattern below could cut a registered secret (one with a space) so
        // it is no longer found whole, and a secret that contains a shorter one ("x-s3cret" and "s3cret") is masked whole.
        var s = text;
        foreach (var secret in KnownSecrets.Keys.OrderByDescending(x => x.Length))
            if (s.Contains(secret, StringComparison.Ordinal)) s = s.Replace(secret, "***", StringComparison.Ordinal);
        // URL-encoded pairs (PT6-6: password%3Dx): find the values in a decoded copy, then mask them as they stand here.
        if (s.Contains('%'))
        {
            var values = new List<string>();
            Patterns(Unescape(s), values);
            foreach (var v in values.Where(v => v.Length >= MinRegisteredLength && v != "***").Distinct().OrderByDescending(v => v.Length))
                foreach (var form in new[] { Uri.EscapeDataString(v), v })
                    if (s.Contains(form, StringComparison.OrdinalIgnoreCase)) s = s.Replace(form, "***", StringComparison.OrdinalIgnoreCase);
        }
        s = Patterns(s, null);
        foreach (var path in KnownPaths.Keys)
        {
            if (s.Contains(path, StringComparison.Ordinal)) s = s.Replace(path, "<file>", StringComparison.Ordinal);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (dir is { Length: > 1 } && s.Contains(dir, StringComparison.Ordinal)) s = s.Replace(dir, "<folder>", StringComparison.Ordinal);
        }
        return s;
    }

    static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException) { return s; }
    }

    /// <summary>The key / value patterns; <paramref name="masked"/> (if given) collects each value they mask.</summary>
    static string Patterns(string s, List<string>? masked)
    {
        s = KeyValueSecret().Replace(s, m =>
        {
            if (m.Groups["keep"].Success) return m.Value;
            masked?.Add(m.Groups["value"].Value.Trim());
            return m.Groups["eq"].Success ? $"{m.Groups["key"].Value}=***" : $"{m.Groups["key"].Value}: ***";
        });
        s = UriUserInfo().Replace(s, m => { masked?.Add(m.Groups[2].Value); return $"{m.Groups[1].Value}:***@"; });
        s = AuthorizationHeader().Replace(s, m => $"{m.Groups[1].Value}: ***");
        s = JsonSecretField().Replace(s, m => $"{m.Groups[1].Value}\"***\"");
        s = JsonSecretArray().Replace(s, m => $"{m.Groups[1].Value}[\"***\"]");
        s = EscapedJsonSecretField().Replace(s, m =>
        {
            var key = m.Groups[1].Value;
            var quote = key[..(key.IndexOf('"') + 1)]; // the backslashes and quote this depth uses
            return $"{key}{quote}***{quote}";
        });
        s = ArgvJson().Replace(s, m => $"{m.Groups[1].Value}***\"");
        s = ArgvBare().Replace(s, m => $"{m.Groups[1].Value}***");
        s = AccessToken().Replace(s, m => $"{m.Groups[1].Value}=***");
        return s;
    }
}
