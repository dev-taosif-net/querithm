using System.Data.Common;

namespace Querithm.Contract.Providers;

// Schema compare: an engine-neutral model each provider reads from its catalog, normalisation on the
// dialect's own tokens (never regex, §15 rule 6), and the DDL each migration step needs.

public sealed record ModelColumn
{
    public required string Name { get; init; }
    /// <summary>Display type as the engine prints it (nvarchar(50), numeric(10,2), [dbo].[Phone]).</summary>
    public required string Type { get; init; }
    public bool Nullable { get; init; }
    public string? Default { get; init; }
    /// <summary>SQL Server default constraint name (null on PostgreSQL).</summary>
    public string? DefaultName { get; init; }
    /// <summary>The server generated <see cref="DefaultName"/>: a new default is created without a name.</summary>
    public bool DefaultSystemNamed { get; init; }
    /// <summary>Computed / generated expression.</summary>
    public string? Computed { get; init; }
    public bool Persisted { get; init; }
    /// <summary>Columns a computed expression reads: altering one of them needs this column dropped and re-added.</summary>
    public string[]? Uses { get; init; }
    /// <summary>SQL Server "seed,increment"; PostgreSQL "always" or "by default".</summary>
    public string? Identity { get; init; }
    /// <summary>Only when it differs from the database (SQL Server) or type (PostgreSQL) default.</summary>
    public string? Collation { get; init; }
}

public sealed record ModelKey
{
    public required string Name { get; init; }
    public bool SystemNamed { get; init; }
    public required string[] Columns { get; init; }
    public bool[]? Descending { get; init; }
    /// <summary>SQL Server CLUSTERED / NONCLUSTERED.</summary>
    public string? Kind { get; init; }
}

public sealed record ModelCheck
{
    public required string Name { get; init; }
    public bool SystemNamed { get; init; }
    /// <summary>SQL Server: the expression; PostgreSQL: pg_get_constraintdef (CHECK (…) [NOT VALID]).</summary>
    public required string Expression { get; init; }
    /// <summary>Columns the expression reads (best effort), for dependency ordering.</summary>
    public string[]? Columns { get; init; }
}

public sealed record ModelIndex
{
    public required string Name { get; init; }
    public bool Unique { get; init; }
    public required string[] Columns { get; init; }
    public bool[]? Descending { get; init; }
    public string[]? Include { get; init; }
    public string? Filter { get; init; }
    /// <summary>SQL Server CLUSTERED / NONCLUSTERED; PostgreSQL access method.</summary>
    public string? Kind { get; init; }
    /// <summary>PostgreSQL pg_get_indexdef (used as the DDL and, without the name, as the shape).</summary>
    public string? Definition { get; init; }
}

public sealed record ModelFk
{
    public required string Name { get; init; }
    public bool SystemNamed { get; init; }
    public required string[] Columns { get; init; }
    public required string RefSchema { get; init; }
    public required string RefTable { get; init; }
    public required string[] RefColumns { get; init; }
    /// <summary>NO ACTION, CASCADE, SET NULL, SET DEFAULT, RESTRICT.</summary>
    public string OnDelete { get; init; } = "NO ACTION";
    public string OnUpdate { get; init; } = "NO ACTION";
    public bool Deferrable { get; init; }
}

public sealed record ModelTable
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required ModelColumn[] Columns { get; init; }
    public ModelKey? PrimaryKey { get; init; }
    public ModelKey[] Uniques { get; init; } = [];
    public ModelCheck[] Checks { get; init; } = [];
    public ModelIndex[] Indexes { get; init; } = [];
    public ModelFk[] Fks { get; init; } = [];
}

public enum ModuleKind { View, MaterializedView, Function, Procedure, Trigger }

public sealed record ModelRef(string Schema, string Name);

public sealed record ModelModule
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public ModuleKind Kind { get; init; }
    /// <summary>PostgreSQL identity arguments of a routine (overloads are separate objects).</summary>
    public string? Signature { get; init; }
    /// <summary>PostgreSQL routine result type: CREATE OR REPLACE cannot change it.</summary>
    public string? Returns { get; init; }
    /// <summary>Null when the server does not reveal it (WITH ENCRYPTION).</summary>
    public string? Definition { get; init; }
    public bool SchemaBound { get; init; }
    /// <summary>The table of a trigger.</summary>
    public string? Table { get; init; }
    /// <summary>View output columns "name type" (PostgreSQL: CREATE OR REPLACE VIEW may only append columns).</summary>
    public string[]? Columns { get; init; }
    /// <summary>Objects the module uses, from the server's dependency catalog.</summary>
    public ModelRef[] References { get; init; } = [];
}

public sealed record ModelSequence
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Start { get; init; }
    public required string Increment { get; init; }
    public string? Min { get; init; }
    public string? Max { get; init; }
    public bool Cycle { get; init; }
}

public sealed record ModelType
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    /// <summary>alias (SQL Server), enum or domain (PostgreSQL).</summary>
    public required string Kind { get; init; }
    /// <summary>Enum labels in order.</summary>
    public string[]? Values { get; init; }
    /// <summary>The rest of the definition (alias/domain base type, NULL-ability, default, checks), engine syntax.</summary>
    public string? Definition { get; init; }
}

public sealed record SchemaModel
{
    public const int CurrentFormat = 1;
    public int Format { get; init; } = CurrentFormat;
    public Engine Engine { get; init; }
    public string? ServerVersion { get; init; }
    public int MajorVersion { get; init; }
    public required string Database { get; init; }
    public long CapturedAt { get; init; }
    public required string[] Schemas { get; init; }
    public string[] Extensions { get; init; } = [];
    public required ModelTable[] Tables { get; init; }
    public required ModelModule[] Modules { get; init; }
    public ModelSequence[] Sequences { get; init; } = [];
    public ModelType[] Types { get; init; } = [];
    /// <summary>What this reader does not compare, with the count ("2 table types are not compared").</summary>
    public string[] NotCompared { get; init; } = [];
}

public enum MigrationPhase
{
    /// <summary>Statements that must commit before the script's transaction uses them (PostgreSQL enum values).</summary>
    BeforeTransaction = 0,
    /// <summary>1. FKs and triggers that are removed or touch altered columns.</summary>
    DropFks = 1,
    /// <summary>2. Removed modules, and dependents of altered columns (dependents first).</summary>
    DropModules = 2,
    /// <summary>3. Schemas, extensions, types, sequences.</summary>
    Prerequisites = 3,
    /// <summary>4. CREATE / ALTER / DROP TABLE.</summary>
    Tables = 4,
    /// <summary>5. Primary keys, unique keys, checks, indexes.</summary>
    Keys = 5,
    /// <summary>6. Foreign keys.</summary>
    Fks = 6,
    /// <summary>7. Views, routines, triggers in dependency order.</summary>
    Modules = 7,
}

public sealed record MigrationStep(MigrationPhase Phase, string Sql, string Comment);

/// <summary>A provider's schema-compare support. Null on <see cref="IDbProvider.SchemaCompare"/> degrades with a reason.</summary>
public interface ISchemaCompareDialect
{
    /// <summary>Whole-database model (all user schemas); Core filters to one schema when asked.</summary>
    Task<SchemaModel> ReadModelAsync(DbConnection connection, CancellationToken ct);

    /// <summary>How the engine compares unquoted names (SQL Server: case-insensitive; PostgreSQL: exact).</summary>
    StringComparer Names { get; }

    /// <summary>DDL is transactional (both engines today; MySQL commits implicitly).</summary>
    bool TransactionalDdl { get; }

    /// <summary>
    /// Phase D2: a database is one schema (MySQL / MariaDB), so two models of different databases compare object by object
    /// across their single schemas without the user naming them. False elsewhere: a model there holds many schemas.
    /// </summary>
    bool DatabaseIsSchema => false;

    /// <summary>Token text without whitespace or comments, keywords and plain names case-folded, redundant outer parentheses removed: ((0)) = (0).</summary>
    string NormalizeExpression(string expression);

    /// <summary>Module text without whitespace, comments and the CREATE / CREATE OR ALTER / OR REPLACE / ALTER header.</summary>
    string NormalizeBody(string definition);

    /// <summary>
    /// The exact text after the CREATE / CREATE OR ALTER / OR REPLACE / ALTER header: SQL Server stores the header as it
    /// was typed, so a module created by a migration differs from its source only there.
    /// </summary>
    string WithoutHeader(string definition);

    /// <summary>Rewrites qualified references <c>from.x</c> to <c>to.x</c> on the token stream (schema-to-schema compares).</summary>
    string RenameSchema(string sql, string from, string to);

    // ---------------------------------------------------------------- DDL (names are quoted by the dialect)

    string CreateSchema(string schema);
    string DropSchema(string schema);
    string CreateExtension(string name);
    string DropExtension(string name);
    string CreateType(ModelType type);
    string DropType(ModelType type);
    /// <summary>Adds an enum value (PostgreSQL); runs before the transaction, since a value added in one cannot be used in it.</summary>
    string? AddEnumValue(ModelType type, string value, string? after);
    string CreateSequence(ModelSequence s);
    string AlterSequence(ModelSequence from, ModelSequence to);
    string DropSequence(ModelSequence s);

    /// <summary>CREATE TABLE with columns, defaults, primary and unique keys and checks; no indexes or FKs.</summary>
    string CreateTable(ModelTable t);
    string DropTable(ModelTable t);
    string AddColumn(ModelTable t, ModelColumn c);
    string DropColumn(ModelTable t, ModelColumn c);
    string RenameColumn(ModelTable t, string from, string to);
    /// <summary>Type, NULL-ability and collation; defaults are handled separately. Empty when nothing of that changed.</summary>
    string[] AlterColumn(ModelTable t, ModelColumn from, ModelColumn to);
    /// <summary>Why the change from → to cannot be scripted as ALTER COLUMN (identity on SQL Server), or null.</summary>
    string? AlterColumnRefusal(ModelColumn from, ModelColumn to);
    string? DropDefault(ModelTable t, ModelColumn c);
    string? AddDefault(ModelTable t, ModelColumn c);
    /// <summary>SQL Server cannot change a column's type under an index, key, check or default; PostgreSQL rebuilds them itself.</summary>
    bool AlterTypeNeedsDependentsDropped { get; }
    /// <summary>
    /// SQL Server refuses to rename a column while computed columns, checks, filtered indexes or schema-bound modules
    /// use it (measured on 2022, §16): they go first and come back after. PostgreSQL follows the rename itself.
    /// </summary>
    bool RenameColumnNeedsDependentsDropped { get; }

    string AddKey(ModelTable t, ModelKey k, bool primary);
    string DropConstraint(ModelTable t, string name);
    string AddCheck(ModelTable t, ModelCheck c);
    string CreateIndex(ModelTable t, ModelIndex i);
    string DropIndex(ModelTable t, ModelIndex i);
    string AddFk(ModelTable t, ModelFk f);

    /// <summary>CREATE OR ALTER / CREATE OR REPLACE where the target version supports it, else CREATE (after a drop).</summary>
    string CreateModule(ModelModule m, int targetMajor);
    /// <summary>The module must be dropped before <see cref="CreateModule"/> (no OR REPLACE for it, or the change needs it).</summary>
    bool NeedsDropBeforeCreate(ModelModule? existing, ModelModule wanted, int targetMajor);
    string DropModule(ModelModule m);
    /// <summary>
    /// The dependent stops its dependencies from being dropped or their columns from being altered (SQL Server:
    /// schema-bound modules; PostgreSQL: anything the server records a dependency for).
    /// </summary>
    bool DependencyBlocks(ModelModule dependent);
    /// <summary>Re-binds a non-schema-bound module after the tables it reads changed (SQL Server sp_refreshsqlmodule), or null.</summary>
    string? RefreshModule(ModelModule m);

    /// <summary>Renames a table, view, routine, type or sequence.</summary>
    string RenameObject(RefKind kind, string schema, string name, string newName);

    /// <summary>Renames identifier <paramref name="from"/> wherever it appears in an expression (a computed column, check or filter of one table), on tokens.</summary>
    string RenameIdentifier(string expression, string from, string to);

    /// <summary>The finished script: one transaction with the engine's guards; BeforeTransaction steps ahead of it.</summary>
    string Wrap(IReadOnlyList<MigrationStep> steps, string header);
}
