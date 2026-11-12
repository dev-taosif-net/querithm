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
    /// release-smoke passed on every RID). Oracle (Phase D4) stays behind it: D4 S13's release gates ran on
    /// 2026-11-12 and the owner kept it hidden until CI release-smoke has passed on every RID.
    /// </summary>
    public static Engine[] BehindFlag => [Engine.Oracle];

    public static IDbProvider[] Providers(string dataDir) =>
    [
        new Querithm.Providers.SqlServer.SqlServerProvider(), new Querithm.Providers.Postgres.PostgresProvider(),
        new Querithm.Providers.Sqlite.SqliteProvider(), new Querithm.Providers.MySql.MySqlProvider(),
        new Querithm.Providers.DuckDb.DuckDbProvider(dataDir), new Querithm.Providers.Oracle.OracleProvider(dataDir),
    ];
}
