using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Oracle;

/// <summary>
/// The Oracle services a later D4 session implements — plans (S09), scripting (S11; packages are <see cref="OracleScripter"/> since S08), grid edit and import (S10): each call
/// refuses with the reason (metadata is <see cref="OracleMetadata"/> since D4 S05). <see cref="OracleProvider.FeatureRefusal"/> refuses the features that use
/// them; they exist so the engine compiles end to end and each session replaces one. Transactions are
/// <see cref="OracleTransactions"/> since D4 S03.
/// </summary>
internal sealed class OracleUnavailable : IPlanProvider, IObjectScripter, IDataEditDialect, IImportDialect
{
    public static readonly OracleUnavailable Instance = new();
    internal const string Reason = "This is not available for Oracle in this build of Querithm yet.";
    static NotSupportedException Refused() => new(Reason);
    static Task<T> RefusedTask<T>() => Task.FromException<T>(Refused());

    // ---- plans ----
    public Task<PlanResult> GetPlanAsync(DbConnection connection, string sql, PlanMode mode, CancellationToken ct) => RefusedTask<PlanResult>();
    /// <summary>InvalidOperationException: plan.parse reports it as "not a plan Querithm can read".</summary>
    public PlanTree Parse(string raw) => throw new InvalidOperationException("Plans for Oracle are not available in this build of Querithm yet.");
    public string? Refusal(string sql, PlanMode mode) => "Plans for Oracle are not available in this build of Querithm yet.";

    // ---- scripting ----
    public Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, CancellationToken ct) => RefusedTask<string>();

    // ---- grid edit (Feature.DataEdit refused) ----
    public string Quote(string identifier) => throw Refused();
    public string Placeholder(int index) => throw Refused();
    public string TypedPlaceholder(int index, string? columnType) => throw Refused();
    public void AddValue(DbCommand command, int index, string? columnType, GridValue value) => throw Refused();
    public (string? Kind, string? Column) Token(SnapObject table) => (null, null);
    public string TokenText(string alias, string column) => throw Refused();
    public string TokenMatch(string alias, string column, string valueExpr) => throw Refused();
    public string? TokenType => null;
    public string NullSafeEquals(string left, string right) => throw Refused();
    public string ValueText(string expr, string? columnType) => throw Refused();
    public string? LockReason(SnapColumn column) => Reason;
    public bool Comparable(string? columnType) => false;
    public bool ExactKey(string? columnType) => false;
    public Task<long> ExecuteCountedAsync(DbCommand command, CancellationToken ct) => RefusedTask<long>();
    public string InsertReturningKey(string table, IReadOnlyList<(string Column, string ValueExpr)> columns, IReadOnlyList<(string Column, string? Type, bool Generated)> key) => throw Refused();
    public string PreviewLiteral(string? columnType, GridValue value) => throw Refused();
    public string PreviewTerminator => ";";
    public string? ConcurrencyNote(string? tokenKind) => null;

    // ---- import (Feature.Import refused) ----
    public string TypeFor(ImportColumnProfile column) => throw Refused();
    public ImportFit Check(ImportColumnProfile column, SnapColumn target) => throw Refused();
    public bool NullUsesDefault => false;
    /// <summary>Oracle stores '' as NULL in VARCHAR2 / CHAR, so the answer depends on the type rules S10 adds.</summary>
    public bool BlankIsNull(string? type) => throw Refused();
    public string CreateTable(string schema, string table, IReadOnlyList<(string Name, string Type, bool NotNull)> columns, IReadOnlyList<string> primaryKey) => throw Refused();
    public string AlterColumnType(string schema, string table, SnapColumn column, string newType) => throw Refused();
    public string AddColumn(string schema, string table, string column, string type) => throw Refused();
    public IBulkLoader? Loader => null;
}
