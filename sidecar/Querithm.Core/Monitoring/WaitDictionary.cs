using System.Text.Json;
using System.Text.Json.Serialization;
using Querithm.Contract;

namespace Querithm.Core.Monitoring;

public sealed record WaitEntry(string Category, string Meaning, string[] Causes, string[] Check);

public sealed record WaitDictionaryFile(int Version, Dictionary<string, WaitEntry> Sqlserver, Dictionary<string, WaitEntry> Postgres);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WaitDictionaryFile))]
internal sealed partial class WaitDictionaryJsonContext : JsonSerializerContext;

/// <summary>
/// Offline wait-type dictionary (B3b): an embedded resource written in-house, loaded with source-generated JSON (no
/// reflection, safe under trimming — W10). Unknown waits get "no description yet" and never fail.
/// </summary>
public sealed class WaitDictionary
{
    public const string ResourceName = "Querithm.Core.Monitoring.WaitDictionary.json";
    static readonly Lazy<WaitDictionary> Shared = new(() => Load());

    readonly Dictionary<string, WaitEntry> _sqlServer;
    readonly Dictionary<string, WaitEntry> _postgres;

    WaitDictionary(WaitDictionaryFile file)
    {
        _sqlServer = new(file.Sqlserver, StringComparer.OrdinalIgnoreCase);
        _postgres = new(file.Postgres, StringComparer.Ordinal);
    }

    public static WaitDictionary Default => Shared.Value;

    public IReadOnlyDictionary<string, WaitEntry> SqlServer => _sqlServer;
    public IReadOnlyDictionary<string, WaitEntry> Postgres => _postgres;

    public static WaitDictionary Load()
    {
        using var stream = typeof(WaitDictionary).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException("The wait dictionary resource is missing.");
        var file = JsonSerializer.Deserialize(stream, WaitDictionaryJsonContext.Default.WaitDictionaryFile)
                   ?? throw new InvalidOperationException("The wait dictionary is empty.");
        return new WaitDictionary(file);
    }

    public WaitInfo Info(Engine engine, string waitType)
    {
        // Phase D0e: an engine without a dictionary of its own knows no wait type (never another engine's descriptions).
        var map = engine switch { Engine.Pg => _postgres, Engine.Mssql => _sqlServer, _ => new Dictionary<string, WaitEntry>() };
        if (!string.IsNullOrEmpty(waitType) && map.TryGetValue(waitType, out var e))
            return new WaitInfo { WaitType = waitType, Known = true, Category = e.Category, Meaning = e.Meaning, Causes = e.Causes, Check = e.Check };
        return new WaitInfo { WaitType = waitType, Known = false, Meaning = "no description yet" };
    }
}
