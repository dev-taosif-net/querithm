using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Oracle;

/// <summary>
/// The Oracle skeleton's metadata, plans, scripting, grid edit, import and transaction services (D4 S01): each call refuses
/// with the reason. Core never reaches them while the profile cannot open and <see cref="OracleProvider.FeatureRefusal"/>
/// refuses the features; they exist so the engine compiles end to end and a later session replaces one at a time.
/// </summary>
internal sealed class OracleUnavailable : IMetadataProvider, IPlanProvider, IObjectScripter, IDataEditDialect, IImportDialect, ITransactionControl
{
    public static readonly OracleUnavailable Instance = new();
    const string Reason = OracleProvider.NotConnectingYet;
    static NotSupportedException Refused() => new(Reason);
    static Task<T> RefusedTask<T>() => Task.FromException<T>(Refused());

    // ---- metadata ----
    public Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection connection, CancellationToken ct) => RefusedTask<IReadOnlyList<ObjectStamp>>();
    public Task<MetadataBundle> GetDetailsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => RefusedTask<MetadataBundle>();
    public Task<string?> GetDefinitionAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) => RefusedTask<string?>();
    public Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) =>
        RefusedTask<IReadOnlyList<(long Id, string? Definition)>>();
    public Task<Dependency[]> GetDependenciesAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) => RefusedTask<Dependency[]>();
    public string ToEditableScript(string definition, Capabilities caps) => throw Refused();
    public Task<RowCount[]> GetRowCountsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => RefusedTask<RowCount[]>();

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

    // ---- transactions (Feature.Transactions refused) ----
    /// <summary>Oracle's documented facts (ODP.NET autocommits outside a transaction; DDL commits before and after); S03 verifies them against the fixture.</summary>
    public TransactionTraits Traits { get; } = new()
    {
        AutocommitDefault = true, DdlCommitsImplicitly = true, Savepoints = true, ReleaseSavepoint = false, ReadOnlyTransactions = true,
        FailedStatementAbortsTransaction = false, UserStatements = ["COMMIT", "ROLLBACK", "SAVEPOINT", "SET TRANSACTION"],
    };
    public Task BeginAsync(DbConnection connection, CancellationToken ct) => Task.FromException(Refused());
    public Task<bool> BeginReadOnlyAsync(DbConnection connection, CancellationToken ct) => RefusedTask<bool>();
    public Task CommitAsync(DbConnection connection, CancellationToken ct) => Task.FromException(Refused());
    public Task RollbackAsync(DbConnection connection, CancellationToken ct) => Task.FromException(Refused());
    public Task SavepointAsync(DbConnection connection, string name, CancellationToken ct) => Task.FromException(Refused());
    public Task RollbackToSavepointAsync(DbConnection connection, string name, CancellationToken ct) => Task.FromException(Refused());
    public Task ReleaseSavepointAsync(DbConnection connection, string name, CancellationToken ct) => Task.FromException(Refused());
    public Task<TxState> StateAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(TxState.None);
}
