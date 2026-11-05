using System.Buffers;
using System.Globalization;
using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Sessions;

/// <summary>
/// Engine-neutral profile rules (Phase D0a): which sign-ins need a secret, and which engine/location/sign-in combinations
/// may reach a driver. The webview mirrors <see cref="NeedsSecret"/> in src/connections/auth.ts; both enumerate every
/// <see cref="AuthKind"/>, so a new kind needs a decision on each side.
/// </summary>
public static class ProfileRules
{
    /// <summary>The sign-in sends a password or token the shell keeps (keyring or memory).</summary>
    public static bool NeedsSecret(AuthKind auth) => auth switch
    {
        AuthKind.Sql or AuthKind.Password or AuthKind.EntraToken => true,
        AuthKind.Windows or AuthKind.Kerberos or AuthKind.EntraInteractive or AuthKind.EntraDeviceCode or AuthKind.None => false,
        _ => throw new ArgumentOutOfRangeException(nameof(auth), auth, null),
    };

    public static bool NeedsSecret(Profile p) => NeedsSecret(p.Auth);

    /// <summary>The sign-in asks the user (a browser, a device code): it never runs unattended before the user signed in once.</summary>
    public static bool Interactive(AuthKind auth) => auth is AuthKind.EntraInteractive or AuthKind.EntraDeviceCode;

    public static LocationKind LocationOf(Profile p) => p.Location ?? LocationKind.Server;

    /// <summary>
    /// Null when the profile may be opened, else the reason: the connection rules of <see cref="Check(Profile, IDbProvider?, bool)"/>
    /// without the input rules, so a stored profile keeps opening when only its display text breaks a newer input rule. A
    /// profile the store could not read is never valid.
    /// </summary>
    public static string? Validate(Profile p, IDbProvider? provider) => Check(p, provider, input: false)?.Message;

    /// <summary>
    /// The refusal for a profile the webview sent (profile.save, profile.test), with the profile field it is about when it
    /// is about one (the editor shows it there).
    /// </summary>
    public static ProfileProblem? Check(Profile p, IDbProvider? provider) => Check(p, provider, input: true);

    /// <summary>
    /// Checks what holds for every engine, then the engine's own rules (<see cref="IDbProvider.ValidateProfile"/>). The
    /// connection rules always apply: known enum values (PT8-6), the address (L25, PT9-2), the timeouts (PT8-2) and local CA
    /// and key paths (PT5-3). <paramref name="input"/> adds the rules for text the webview sends (PT8-1, PT1-2): sizes,
    /// control and invisible characters, the color's shape and the option allowlist.
    /// </summary>
    public static ProfileProblem? Check(Profile p, IDbProvider? provider, bool input)
    {
        if (p.Unsupported is { } reason) return new(reason);
        if (!Enum.IsDefined(p.Engine)) return new("Unknown engine.");
        if (!Enum.IsDefined(p.Auth)) return new("Unknown sign-in.");
        if (p.Location is { } loc && !Enum.IsDefined(loc)) return new("Unknown location.");
        if (!Enum.IsDefined(p.TlsMode)) return new("Unknown encryption (TLS) mode.");
        if (!Enum.IsDefined(p.Environment)) return new("Unknown environment.");
        if (p.Ssh is { } ssh && !Enum.IsDefined(ssh.Auth)) return new("Unknown SSH sign-in.");
        if (input && Input(p) is { } text) return text;
        if (Timeouts(p, input) is { } timeout) return timeout;
        if (!string.IsNullOrEmpty(p.CaPath) && !LocalPath(p.CaPath))
            return new("The CA certificate must be a file on this computer, named by its full path (not a network share, device path or URL).", "caPath");
        if (provider == null) return new(Unavailable(p.Engine));
        switch (LocationOf(p))
        {
            case LocationKind.File:
                if (string.IsNullOrWhiteSpace(p.File?.GrantId)) return new("Pick the database file.", "file");
                if (p.Ssh != null) return new("A database file is opened on this computer; it cannot go through an SSH tunnel.");
                break;
            case LocationKind.Memory:
                if (p.File != null) return new("An in-memory database has no file.");
                if (p.Ssh != null) return new("An in-memory database lives on this computer; it cannot go through an SSH tunnel.");
                break;
            default:
                if (p.File != null) return new("A server profile has no database file.");
                if (Address(p) is { } bad) return bad;
                break;
        }
        return provider.ValidateProfile(p) is { } own ? new(own) : null;
    }

    /// <summary>The longest host (or host list) a profile may name; a DNS name has at most 253 characters.</summary>
    public const int MaxHostLength = 1024;
    /// <summary>The longest profile or group name.</summary>
    public const int MaxNameLength = 256;
    /// <summary>The longest of any other profile text (user, database, paths, file names).</summary>
    public const int MaxTextLength = 4096;
    /// <summary>The most entries <see cref="Profile.Options"/> may hold.</summary>
    public const int MaxOptions = 32;
    /// <summary>The longest connect timeout, seconds (0: the driver's "no limit"; profile.test caps it at <see cref="MaxTestTimeoutS"/>).</summary>
    public const int MaxTimeoutS = 600;
    /// <summary>The longest command timeout, seconds (24 hours); 0 = none.</summary>
    public const int MaxCommandTimeoutS = 86_400;
    /// <summary>The longest a profile.test connect may take, whatever the profile says.</summary>
    public const int MaxTestTimeoutS = 120;
    /// <summary>
    /// The longest profile id: the shell's keyring accounts are "&lt;id&gt;", "&lt;id&gt;:timed", "&lt;id&gt;:ssh" and
    /// "&lt;id&gt;:ssh:timed", at most 64 characters each (keyring.rs validate_id). The sidecar assigns 26-character ULIDs.
    /// </summary>
    public const int MaxIdLength = 54;

    /// <summary>
    /// A profile id the sidecar could have assigned (PT8-4): 1–<see cref="MaxIdLength"/> ASCII letters, digits, '-' or '_'.
    /// Never ':' (the shell's keyring suffixes), '/', '.', NUL or anything else a path or account name reads specially.
    /// </summary>
    public static bool ValidId(string? id) =>
        id is { Length: >= 1 and <= MaxIdLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// The one text check for profile and group fields (PT8-1, PT8-3, PT9-2): at most <paramref name="max"/> characters,
    /// no control characters (Cc) or broken UTF-16, and with <paramref name="visible"/> also no format, bidi or zero-width
    /// characters (Cf) and no line or paragraph separators (Zl, Zp), so one name cannot draw as another. Null when it passes.
    /// </summary>
    public static ProfileProblem? Text(string? value, string field, string label, int max = MaxTextLength, bool visible = true, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) return new($"Enter the {label}.", field);
            if (string.IsNullOrEmpty(value)) return null;
        }
        if (value!.Length > max || !CleanText(value, visible))
            return new(visible
                ? $"The {label} must be at most {max} characters, without line breaks, control characters or invisible formatting characters."
                : $"The {label} must be at most {max} characters, without line breaks or control characters.", field);
        return null;
    }

    /// <summary>A group as group.save may store it (PT8-3).</summary>
    public static ProfileProblem? CheckGroup(ProfileGroup? g)
    {
        if (g == null) return new("group.save needs a group.");
        if (Text(g.Name, "name", "group name", MaxNameLength, required: true) is { } name) return name;
        if (g.Environment is { } env && !Enum.IsDefined(env)) return new("Unknown environment.", "environment");
        return Color(g.Color);
    }

    static bool CleanText(string value, bool visible)
    {
        for (var i = 0; i < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(i), out var rune, out var used) != OperationStatus.Done) return false;
            i += used;
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control) return false;
            if (visible && category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
        }
        return true;
    }

    /// <summary>The input rules: text the webview sends, checked before it is stored or tested.</summary>
    static ProfileProblem? Input(Profile p)
    {
        if (Text(p.Name, "name", "name", MaxNameLength, required: true) is { } name) return name;
        if (Text(p.GroupName, "groupName", "group name", MaxNameLength) is { } group) return group;
        if (Text(p.Username, "username", "user name") is { } user) return user;
        if (Text(p.DatabaseDefault, "databaseDefault", "database name") is { } db) return db;
        if (Text(p.File?.GrantId, "file", "file grant", visible: false) is { } grant) return grant;
        if (Text(p.File?.DisplayName, "file", "file name", visible: false) is { } file) return file;
        if (Color(p.Color) is { } color) return color;
        if (p.Ssh is { } ssh && Text(ssh.Username, "sshUsername", "SSH user name") is { } sshUser) return sshUser;
        return Options(p.Options);
    }

    static ProfileProblem? Color(string? color) =>
        string.IsNullOrEmpty(color) || CssColor(color) ? null : new("The color must be a CSS color such as #3b82f6, a color name, or rgb(…) / hsl(…).", "color");

    static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");
    static readonly SearchValues<char> ColorArguments = SearchValues.Create("0123456789 ,.%/+-degturna");

    /// <summary>A CSS color the UI may put in a style: #hex, a color name, var(--name), or rgb()/rgba()/hsl()/hsla() of plain numbers.</summary>
    static bool CssColor(string c)
    {
        if (c.Length > 64) return false;
        if (c[0] == '#') return c.Length is 4 or 5 or 7 or 9 && !c.AsSpan(1).ContainsAnyExcept(HexDigits);
        if (c.All(char.IsAsciiLetter)) return c.Length <= 32;
        if (c.StartsWith("var(--", StringComparison.Ordinal) && c.EndsWith(')'))
            return c.Length > 7 && c[6..^1].All(x => char.IsAsciiLetterOrDigit(x) || x == '-');
        var open = c.IndexOf('(');
        if (open < 0 || !c.EndsWith(')') || c[..open] is not ("rgb" or "rgba" or "hsl" or "hsla")) return false;
        return !c.AsSpan(open + 1, c.Length - open - 2).ContainsAnyExcept(ColorArguments);
    }

    /// <summary>
    /// The option keys the providers read (PT1-2), each with its value rule; anything else is refused, so a password or other
    /// text never lands in store.db through options. A provider that reads a new key adds it here.
    /// </summary>
    static readonly Dictionary<string, Func<string, bool>> OptionRules = new(StringComparer.Ordinal)
    {
        ["appName"] = v => v.Length <= 128 && CleanText(v, visible: false),
        ["hostNameInCertificate"] = v => v.Length is >= 1 and <= 253 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '*' or ':'),
        ["applicationIntent"] = v => v is "readOnly" or "readWrite",
        ["multiSubnetFailover"] = v => v is "true" or "false",
        ["connectRetryCount"] = v => v.Length is >= 1 and <= 3 && v.All(char.IsAsciiDigit) && int.Parse(v, CultureInfo.InvariantCulture) <= 255,
        ["kerberosServiceName"] = v => v.Length is >= 1 and <= 64 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or '@'),
    };

    /// <summary>The option keys a profile may carry.</summary>
    public static IReadOnlyCollection<string> OptionKeys => OptionRules.Keys;

    static ProfileProblem? Options(Dictionary<string, string>? options)
    {
        if (options == null) return null;
        if (options.Count > MaxOptions) return new($"A profile may carry at most {MaxOptions} options.", "options");
        foreach (var (key, value) in options)
        {
            if (!OptionRules.TryGetValue(key, out var rule))
                return new($"'{(key.Length <= 40 && CleanText(key, visible: true) ? key : "(unreadable)")}' is not a connection option Querithm knows.", "options");
            if (value == null || !rule(value)) return new($"The value of the '{key}' option is not one Querithm accepts.", "options");
        }
        return null;
    }

    /// <summary>
    /// The ranges are enforced on what the webview sends; a profile saved before them keeps opening with a long timeout
    /// (the open itself is bounded by the caller's token), and only a negative one, which no driver ever took, is refused.
    /// </summary>
    static ProfileProblem? Timeouts(Profile p, bool input)
    {
        if (!input)
            return p.TimeoutS is < 0 ? new("The connect timeout cannot be negative.", "timeoutS")
                : p.CommandTimeoutS is < 0 ? new("The query timeout cannot be negative.", "commandTimeoutS") : null;
        if (p.TimeoutS is { } t && t is < 0 or > MaxTimeoutS)
            return new($"The connect timeout must be a whole number of seconds from 0 to {MaxTimeoutS}.", "timeoutS");
        if (p.CommandTimeoutS is { } c && c is < 0 or > MaxCommandTimeoutS)
            return new($"The query timeout must be a whole number of seconds from 0 to {MaxCommandTimeoutS} (24 hours); 0 means no limit.", "commandTimeoutS");
        return null;
    }

    static readonly HashSet<string> DosDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A file on this computer named by its full path (PT5-3): never a UNC share (\\server\share, //server), an extended or
    /// device path (\\?\, \\.\, a DOS device name), a URL, or a relative path the driver would resolve against its own
    /// working directory. A driver reading a remote CA trusts whatever the share serves, and signs in to it over SMB.
    /// </summary>
    public static bool LocalPath(string path)
    {
        if (path.Length > MaxTextLength || !CleanText(path, visible: false)) return false;
        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/') return false;
        if (!Path.IsPathFullyQualified(path)) return false;
        if (OperatingSystem.IsWindows())
        {
            if (path.IndexOf(':', 2) >= 0) return false; // an NTFS stream (ca.pem:x) or a second drive
            if (DosDevices.Contains(Path.GetFileNameWithoutExtension(path.TrimEnd(' ', '.')))) return false;
        }
        return true;
    }

    /// <summary>
    /// The address shape every server engine shares (L25): a port of 1–65535 (null: the engine's default) and a host without
    /// control or invisible characters, for the server and for an SSH bastion (whose host and user are required, and whose
    /// key file is local). An empty server host keeps each driver's own default.
    /// </summary>
    static ProfileProblem? Address(Profile p)
    {
        if (p.Port is { } port && !ValidPort(port)) return new("The port must be a whole number from 1 to 65535.", "port");
        if (p.Host is { } host && !ValidHost(host)) return new(HostRule("host"), "host");
        if (p.Ssh is { } ssh)
        {
            if (ssh.Port is { } sp && !ValidPort(sp)) return new("The SSH port must be a whole number from 1 to 65535.", "sshPort");
            if (string.IsNullOrWhiteSpace(ssh.Host)) return new("Enter the SSH server's host name.", "sshHost");
            if (!ValidHost(ssh.Host)) return new(HostRule("SSH host"), "sshHost");
            if (string.IsNullOrWhiteSpace(ssh.Username)) return new("Enter the user name for the SSH server.", "sshUsername");
            if (!string.IsNullOrEmpty(ssh.KeyPath) && !LocalPath(ssh.KeyPath))
                return new("The SSH key must be a file on this computer, named by its full path (not a network share, device path or URL).", "sshKeyPath");
        }
        return null;
    }

    static string HostRule(string what) =>
        $"The {what} must be at most {MaxHostLength} characters, without line breaks, control characters or invisible formatting characters.";

    static bool ValidPort(int port) => port is >= 1 and <= 65535;

    static bool ValidHost(string host) => host.Length <= MaxHostLength && CleanText(host, visible: true);

    public static string Unavailable(Engine engine) => $"The {EngineName(engine)} engine is not available in this build of Querithm.";

    public static string EngineName(Engine engine) => engine switch
    {
        Engine.Mssql => "SQL Server",
        Engine.Pg => "PostgreSQL",
        Engine.Sqlite => "SQLite",
        Engine.MySql => "MySQL / MariaDB",
        Engine.DuckDb => "DuckDB",
        _ => engine.ToString(),
    };
}

/// <summary>
/// Why a profile is refused, and the profile field it is about or null: name, groupName, host, port, file, username,
/// databaseDefault, color, caPath, options, timeoutS, commandTimeoutS, sshHost, sshPort, sshUsername, sshKeyPath, id.
/// </summary>
public sealed record ProfileProblem(string Message, string? Field = null);
