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
        o.Converters.Add(new StrictEnumConverter(JsonNamingPolicy.CamelCase));
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

/// <summary>
/// Enums as camelCase strings, read strictly (PT8-6): exactly one member's name (its camelCase spelling or its declared
/// one, in any case, as <see cref="JsonStringEnumConverter"/> read them), never a number, never a comma list. The stock
/// converter read "mssql,pg" as the bitwise OR of both members even for enums without [Flags], so undefined values
/// (TlsMode 14, SshAuthKind 3) reached the services and the store. Writing an undefined value throws, as before.
/// </summary>
public sealed class StrictEnumConverter(JsonNamingPolicy policy) : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2071",
        Justification = "CanConvert admits only enums, and an enum always has its parameterless constructor (the struct constraint).")]
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Strict<>).MakeGenericType(typeToConvert), policy)!;

    sealed class Strict<T> : JsonConverter<T> where T : struct, Enum
    {
        readonly Dictionary<string, T> _read = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<T, string> _write = [];

        public Strict(JsonNamingPolicy policy)
        {
            foreach (var value in Enum.GetValues<T>())
            {
                var declared = Enum.GetName(value)!;
                var name = policy.ConvertName(declared);
                _write.TryAdd(value, name);
                _read.TryAdd(name, value);
                _read.TryAdd(declared, value);
            }
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String ? Parse(reader.GetString()) : throw Refused();

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(_write.TryGetValue(value, out var name) ? name : throw Undefined(value));

        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Parse(reader.GetString());

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WritePropertyName(_write.TryGetValue(value, out var name) ? name : throw Undefined(value));

        T Parse(string? text) => text != null && _read.TryGetValue(text, out var value) ? value : throw Refused();

        static JsonException Refused() => new($"The value is not one of the names {typeof(T).Name} allows.");

        static JsonException Undefined(T value) =>
            new($"{typeof(T).Name} has no member {Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)}.");
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
