using Querithm.Contract;
using Querithm.Core.Store;

namespace Querithm.Tests;

/// <summary>
/// The engines the shared engine theories cover (Phase D0g): one Engine-typed source instead of a list per test class.
/// SQLite, MySQL / MariaDB and DuckDB, exposed since 0.2.0, are covered by their own per-engine classes
/// (`FileEngines/Sqlite*`, `FileEngines/DuckDb*`, `Integration/MySql*`, the `*IntelligenceTests`), because these theories
/// branch on SQL Server versus PostgreSQL; moving them into the theories is the open D0e test refactor.
/// </summary>
public static class TestEngines
{
    public static readonly Engine[] Active = [Engine.Mssql, Engine.Pg];

    /// <summary>The engines in their wire spelling ("mssql", "pg"), for theories that take the string.</summary>
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var e in Active) data.Add(EnumText.Of(e));
            return data;
        }
    }

    public static TheoryData<Engine> All
    {
        get
        {
            var data = new TheoryData<Engine>();
            foreach (var e in Active) data.Add(e);
            return data;
        }
    }
}
