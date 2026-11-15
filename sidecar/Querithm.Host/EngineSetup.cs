using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Host;

/// <summary>
/// The engines this build ships and which of them stay hidden (Phase D merge model), read by Program and asserted by
/// EngineRegistryTests so the shipped configuration itself is tested, not a copy of it.
/// </summary>
internal static class EngineSetup
{
    /// <summary>
    /// A new engine's provider ships hidden until its release gates pass; QUERITHM_DEV_ENGINES or the dev.engines setting
    /// exposes it. SQLite, MySQL / MariaDB and DuckDB left the flag in 0.2.0 (owner decision 2026-11-01, after
    /// release-smoke passed on every RID). Oracle (Phase D4) left it on 2026-11-15 (owner decision S13-a, after D4 S13's
    /// release gates and CI release-smoke on every RID). No engine is behind it now; the next one (Phase D5+) joins here.
    /// </summary>
    public static Engine[] BehindFlag => [];

    public static IDbProvider[] Providers(string dataDir) =>
    [
        new Querithm.Providers.SqlServer.SqlServerProvider(), new Querithm.Providers.Postgres.PostgresProvider(),
        new Querithm.Providers.Sqlite.SqliteProvider(), new Querithm.Providers.MySql.MySqlProvider(),
        new Querithm.Providers.DuckDb.DuckDbProvider(dataDir), new Querithm.Providers.Oracle.OracleProvider(dataDir),
    ];
}
