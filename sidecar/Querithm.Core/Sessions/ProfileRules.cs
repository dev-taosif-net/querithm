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
    /// Null when the profile may be saved, tested or opened, else the reason. Checks what holds for every engine, then the
    /// engine's own rules (<see cref="IDbProvider.ValidateProfile"/>). A profile the store could not read is never valid.
    /// </summary>
    public static string? Validate(Profile p, IDbProvider? provider) => Check(p, provider)?.Message;

    /// <summary><see cref="Validate"/> with the profile field the refusal is about, when it is about one (the editor shows it there).</summary>
    public static ProfileProblem? Check(Profile p, IDbProvider? provider)
    {
        if (p.Unsupported is { } reason) return new(reason);
        if (!Enum.IsDefined(p.Engine)) return new("Unknown engine.");
        if (!Enum.IsDefined(p.Auth)) return new("Unknown sign-in.");
        if (p.Location is { } loc && !Enum.IsDefined(loc)) return new("Unknown location.");
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

    /// <summary>
    /// The address shape every server engine shares (L25): a port of 1–65535 (null: the engine's default) and a host
    /// without control characters, for the server and for an SSH bastion. An empty host keeps each driver's own default.
    /// </summary>
    static ProfileProblem? Address(Profile p)
    {
        if (p.Port is { } port && !ValidPort(port)) return new("The port must be a whole number from 1 to 65535.", "port");
        if (p.Host is { } host && !ValidHost(host)) return new($"The host must be at most {MaxHostLength} characters, without line breaks or control characters.", "host");
        if (p.Ssh is { } ssh)
        {
            if (ssh.Port is { } sp && !ValidPort(sp)) return new("The SSH port must be a whole number from 1 to 65535.", "sshPort");
            if (!ValidHost(ssh.Host ?? "")) return new($"The SSH host must be at most {MaxHostLength} characters, without line breaks or control characters.", "sshHost");
        }
        return null;
    }

    static bool ValidPort(int port) => port is >= 1 and <= 65535;

    static bool ValidHost(string host) => host.Length <= MaxHostLength && !host.Any(char.IsControl);

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

/// <summary>Why a profile is refused, and the profile field it is about (name, host, port, file, sshHost, sshPort) or null.</summary>
public sealed record ProfileProblem(string Message, string? Field = null);
