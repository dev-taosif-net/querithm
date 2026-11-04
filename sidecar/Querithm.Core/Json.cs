using System.Text.Json;
using System.Text.Json.Serialization;
using Querithm.Contract;
using Querithm.Core.Store;

namespace Querithm.Core;

/// <summary>Wire format for the control plane and settings: camelCase, enums as camelCase strings, nulls omitted.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>Source-generated <see cref="Options"/> metadata for what the stores persist; the trimmed sidecar can't rely on reflection.</summary>
    public static readonly WireJsonContext Context = new(Create());

    /// <summary>Source-generated <see cref="Options"/> metadata for the activity monitor's history and its export.</summary>
    internal static readonly MonitorJsonContext Monitor = new(Create());

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return o;
    }

    /// <summary>
    /// The control plane's options: <see cref="Create"/> with member names bound exactly as the contract spells them
    /// (M1). The shell strips and injects the names it owns (<c>filePath</c>, <c>fileGrant</c>, the secrets) by their exact
    /// spelling, so a case-insensitive bind let <c>{"FilePath": …}</c> from the webview reach the services. Stored
    /// settings and saved schema files keep <see cref="Create"/>.
    /// </summary>
    public static JsonSerializerOptions CreateRpc()
    {
        var o = Create();
        o.PropertyNameCaseInsensitive = false;
        return o;
    }
}

[JsonSerializable(typeof(ProfileGroup))]
[JsonSerializable(typeof(ProfileGroup[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(FkColumnPair[]))]
[JsonSerializable(typeof(Querithm.Contract.Providers.SchemaModel))]
[JsonSerializable(typeof(HistoryRetention))]
public sealed partial class WireJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(MonitorSnapshot))]
[JsonSerializable(typeof(ActivityRow[]))]
[JsonSerializable(typeof(SessionKey[]))]
internal sealed partial class MonitorJsonContext : JsonSerializerContext;

/// <summary>Columns that were always written with the reflection defaults (PascalCase, nulls kept); existing rows depend on it.</summary>
[JsonSerializable(typeof(ProfileStore.StoredOptions))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
