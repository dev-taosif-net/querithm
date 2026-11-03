namespace Querithm.Contract;

/// <summary>A saved connection. Never carries a password (§13 rule 1).</summary>
public sealed record Profile
{
    /// <summary>Empty on create; profile.save assigns a ULID.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Engine Engine { get; init; }
    /// <summary>Null = <see cref="LocationKind.Server"/> (every profile written before Phase D).</summary>
    public LocationKind? Location { get; init; }
    /// <summary>Location File: the grant the shell gave for the picked file. The path never reaches the profile.</summary>
    public FileLocation? File { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public AuthKind Auth { get; init; }
    public string? Username { get; init; }
    public string? DatabaseDefault { get; init; }
    public EnvironmentKind Environment { get; init; }
    /// <summary>CSS color; null = environment default.</summary>
    public string? Color { get; init; }
    public string? GroupName { get; init; }
    public TlsMode TlsMode { get; init; }
    public bool TrustCert { get; init; }
    public string? CaPath { get; init; }
    /// <summary>Connect timeout, seconds (default 15).</summary>
    public int? TimeoutS { get; init; }
    /// <summary>Command timeout, seconds; 0 = none.</summary>
    public int? CommandTimeoutS { get; init; }
    /// <summary>Engine-specific extras: applicationIntent, multiSubnetFailover, entraTenantId, entraClientId, appName.</summary>
    public Dictionary<string, string>? Options { get; init; }
    /// <summary>Connect through an SSH bastion; null = direct. Host/Port above are as the bastion sees the server.</summary>
    public SshSettings? Ssh { get; init; }
    /// <summary>once, session, duration, or forever; null preserves legacy forever behavior.</summary>
    public string? PasswordRetention { get; init; }
    public int? PasswordRetentionMinutes { get; init; }
    public long CreatedAt { get; init; }
    public long UpdatedAt { get; init; }
    /// <summary>
    /// Set by the sidecar when the stored profile names an engine, sign-in or location this build does not know (written
    /// by a newer build, or for an engine that is not available): the reason to show. Such a profile is listed but never
    /// opened, tested or overwritten; it can be deleted.
    /// </summary>
    public string? Unsupported { get; init; }
}

/// <summary>A database file as a profile knows it: the shell's grant id and the file's display name, never its path (§13 rule 6).</summary>
public sealed record FileLocation
{
    public required string GrantId { get; init; }
    /// <summary>The file name shown in the UI (no directories).</summary>
    public string? DisplayName { get; init; }
}

/// <summary>An SSH bastion. Never carries a password or passphrase: those live in the keyring under "&lt;profileId&gt;:ssh".</summary>
public sealed record SshSettings
{
    public required string Host { get; init; }
    /// <summary>Default 22.</summary>
    public int? Port { get; init; }
    public required string Username { get; init; }
    public SshAuthKind Auth { get; init; }
    /// <summary>PrivateKey: the key file (OpenSSH, PEM or PuTTY format), picked by the user.</summary>
    public string? KeyPath { get; init; }
}

/// <summary>A bastion's host key as shown to the user before it is trusted.</summary>
public sealed record SshHostKey
{
    public required string Host { get; init; }
    public int Port { get; init; }
    /// <summary>e.g. ssh-ed25519, ecdsa-sha2-nistp256, rsa-sha2-512.</summary>
    public required string Algorithm { get; init; }
    /// <summary>SHA256 fingerprint, base64 without padding (as OpenSSH prints it after "SHA256:").</summary>
    public required string Fingerprint { get; init; }
    /// <summary>Set when the bastion presents a different key than the trusted one.</summary>
    public string? PreviousFingerprint { get; init; }
}

public sealed record ProfileGroup
{
    public required string Name { get; init; }
    public EnvironmentKind? Environment { get; init; }
    public string? Color { get; init; }
}

public sealed record ProfileRef
{
    public required string ProfileId { get; init; }
}

public sealed record ProfileTestParams
{
    public string? ProfileId { get; init; }
    /// <summary>An unsaved profile from the editor dialog.</summary>
    public Profile? Profile { get; init; }
    /// <summary>Injected by Rust from the keyring for saved profiles, or typed in the dialog.</summary>
    public string? Secret { get; init; }
    /// <summary>SSH password or key passphrase: injected by Rust from the keyring ("&lt;profileId&gt;:ssh"), or typed in the dialog.</summary>
    public string? SshSecret { get; init; }
    /// <summary>A file profile's grant, injected by the shell (Phase D0c).</summary>
    public FileGrant? FileGrant { get; init; }
}

/// <summary>Microsoft Entra device-code sign-in: show the code and URL to the user.</summary>
public sealed record AuthPromptEvent
{
    public required string Message { get; init; }
    public string? VerificationUrl { get; init; }
    public string? UserCode { get; init; }
}

public sealed record ProfileTestResult
{
    public bool Ok { get; init; }
    public string? ServerVersion { get; init; }
    public string? Error { get; init; }
    public double ElapsedMs { get; init; }
    /// <summary>Things that work differently than configured (e.g. TLS host checking through an SSH tunnel).</summary>
    public string[] Notes { get; init; } = [];
    /// <summary>The bastion's host key is not trusted yet (or changed): trust it with ssh.trustHostKey, then test again.</summary>
    public SshHostKey? UntrustedHostKey { get; init; }
}

public sealed record ParseConnectionStringParams
{
    /// <summary>The editor's current profile: identity fields are kept, connection fields the string names are replaced.</summary>
    public required Profile Profile { get; init; }
    /// <summary>ADO.NET keyword string for either engine, or a postgres:// URI. Never stored.</summary>
    public required string ConnectionString { get; init; }
}

public sealed record ParseConnectionStringResult
{
    /// <summary>The merged profile; its engine is the one whose driver recognised the string best.</summary>
    public required Profile Profile { get; init; }
    /// <summary>Password or access token from the string; the editor sends it to the keyring on Save.</summary>
    public string? Secret { get; init; }
    /// <summary>Keywords the driver does not know, or whose value it rejected.</summary>
    public string[] Unknown { get; init; } = [];
    /// <summary>Keywords the driver knows but Querithm sets itself or does not offer (e.g. Pooling).</summary>
    public string[] NotApplied { get; init; } = [];
    /// <summary>
    /// Warnings the editor shows (M3): a TLS setting the string names that is weaker than the engine's default (§13 rule 2).
    /// A TLS keyword the string does not name never changes the profile's mode.
    /// </summary>
    public string[] Notes { get; init; } = [];
}
