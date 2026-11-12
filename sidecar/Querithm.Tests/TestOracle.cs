using Querithm.Contract.Providers;
using Querithm.Host;
using Querithm.Providers.Oracle;

namespace Querithm.Tests;

/// <summary>
/// The Oracle provider as it is on every platform ODP.NET supports. On Intel macOS the public constructor gives the
/// platform refusal (owner decision O6: Oracle listed but disabled with the driver's reason), so a test of what Oracle does
/// once it may run builds its provider here; the refusal itself is tested in <c>OracleEditorSupportTests</c> and by the
/// trimmed self-test (<c>WaitDictionaryTests.W10_…</c>). D4 S13: the first CI run on Intel macOS failed 29 such tests.
/// </summary>
internal static class TestOracle
{
    public static OracleProvider Supported(string? dataDir = null) => new(dataDir, unavailable: null);

    /// <summary>The shipped providers (<see cref="EngineSetup.Providers"/>) with Oracle as on a supported platform.</summary>
    public static IDbProvider[] ShippedProviders(string dataDir) =>
        EngineSetup.Providers(dataDir).Select(p => p is OracleProvider ? Supported(dataDir) : p).ToArray();
}
