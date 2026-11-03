using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Sessions;

/// <summary>
/// Refuses an RPC whose feature the session's provider does not implement (Phase D0a), with the reason the capabilities
/// show; the webview disabling the action is never the only guard.
/// </summary>
public static class FeatureGuard
{
    public static void Require(IDbProvider provider, Feature feature)
    {
        if (provider.FeatureRefusal(feature) is { } reason)
            throw new QuerithmException("unsupported", reason, new Dictionary<string, string> { ["feature"] = EnumNameOf(feature) });
    }

    public static CapabilityInfo Info(IDbProvider provider, Feature feature) =>
        provider.FeatureRefusal(feature) is { } reason ? CapabilityInfo.No(reason) : CapabilityInfo.Yes;

    /// <summary>The provider's static feature set on top of what its server reported.</summary>
    public static Capabilities Apply(IDbProvider provider, Capabilities caps) => caps with
    {
        ChangeDatabase = Info(provider, Feature.ChangeDatabase),
        Transactions = Info(provider, Feature.Transactions),
        DataEdit = Info(provider, Feature.DataEdit),
        Import = Info(provider, Feature.Import),
        SchemaCompare = Info(provider, Feature.SchemaCompare, provider.SchemaCompare != null, "Schema compare is not available for this engine yet."),
        SchemaCompareApply = Info(provider, Feature.SchemaCompareApply, provider.SchemaCompare != null, "Schema compare is not available for this engine yet."),
        DataCompare = Info(provider, Feature.DataCompare, provider.DataCompare != null, "Data compare is not available for this engine yet."),
        DataCompareApply = Info(provider, Feature.DataCompareApply, provider.DataCompare != null, "Data compare is not available for this engine yet."),
        References = Info(provider, Feature.References, provider.References != null, "Find usages and rename are not available for this engine yet."),
        Monitor = Info(provider, Feature.Monitor, provider.Activity != null, "The activity monitor is not available for this engine."),
    };

    /// <summary>The provider's own reason wins (it names the engine), then the missing implementation's generic one.</summary>
    static CapabilityInfo Info(IDbProvider provider, Feature feature, bool implemented, string missing) =>
        provider.FeatureRefusal(feature) is { } reason ? CapabilityInfo.No(reason) : implemented ? CapabilityInfo.Yes : CapabilityInfo.No(missing);

    static string EnumNameOf(Feature f) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(f.ToString());
}
