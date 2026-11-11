using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Oracle;

/// <summary>
/// The Oracle service a later D4 session implements — scripting of every kind but packages (S11; packages are
/// <see cref="OracleScripter"/> since S08): each call refuses with the reason. Grid edit and import are
/// <see cref="OracleDataEdit"/> / <see cref="OracleImport"/> since D4 S10, transactions <see cref="OracleTransactions"/>
/// since S03, metadata <see cref="OracleMetadata"/> since S05.
/// </summary>
internal sealed class OracleUnavailable : IObjectScripter
{
    public static readonly OracleUnavailable Instance = new();
    internal const string Reason = "This is not available for Oracle in this build of Querithm yet.";
    static NotSupportedException Refused() => new(Reason);
    static Task<T> RefusedTask<T>() => Task.FromException<T>(Refused());

    public Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, CancellationToken ct) => RefusedTask<string>();
}
