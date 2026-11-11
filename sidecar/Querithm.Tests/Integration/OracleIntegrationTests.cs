using Oracle.ManagedDataAccess.Client;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S01 driver smoke against the Oracle Free fixture (QUERITHM_IT=1; <c>docker compose --profile oracle up -d --wait
/// oracle</c>). The driver alone, not the provider: connecting through Querithm arrives in D4 S03. Also checks the fixture's
/// two accounts have exactly the privileges the later sessions rely on.
/// </summary>
public sealed class OracleIntegrationTests
{
    internal const int Port = 15210;
    internal const string Service = "FREEPDB1";
    internal const string AppUser = "querithm_app";
    internal const string MonitorUser = "querithm_mon";

    internal static string ConnectionString(string user) => new OracleConnectionStringBuilder
    {
        DataSource = $"//127.0.0.1:{Port}/{Service}", UserID = user, Password = FixtureSecrets.Password(user == AppUser ? "oracle" : "oracle-mon"),
        Pooling = false, ConnectionTimeout = 30,
    }.ConnectionString;

    static async Task<object?> ScalarAsync(string user, string sql)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var c = new OracleConnection(ConnectionString(user));
        await c.OpenAsync(cts.Token);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        return await cmd.ExecuteScalarAsync(cts.Token);
    }

    [IntegrationFact]
    public async Task The_driver_opens_a_connection_to_the_fixture_and_selects_from_dual() =>
        Assert.Equal(1m, Convert.ToDecimal(await ScalarAsync(AppUser, "SELECT 1 FROM DUAL"), System.Globalization.CultureInfo.InvariantCulture));

    [IntegrationFact]
    public async Task The_test_user_is_least_privileged_and_the_monitor_user_reads_sessions()
    {
        // CREATE SESSION + its own schema only: the dictionary's V$ views are out of reach (ORA-00942).
        var denied = await Assert.ThrowsAsync<OracleException>(() => ScalarAsync(AppUser, "SELECT COUNT(*) FROM V$SESSION"));
        Assert.Equal(942, denied.Number);
        Assert.Equal(0m, Convert.ToDecimal(await ScalarAsync(AppUser, "SELECT COUNT(*) FROM SESSION_PRIVS WHERE PRIVILEGE LIKE '%ANY%'"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Convert.ToDecimal(await ScalarAsync(MonitorUser, "SELECT COUNT(*) FROM V$SESSION"), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [IntegrationFact]
    public async Task The_reserved_words_cover_the_servers_list()
    {
        // D4 S02 open item: OracleKeywords against the server's own list. Punctuation entries (!, <=>, [, …) are not words.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var c = new OracleConnection(ConnectionString(MonitorUser));
        await c.OpenAsync(cts.Token);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT keyword FROM v$reserved_words WHERE (reserved = 'Y' OR res_semi = 'Y') AND REGEXP_LIKE(keyword, '^[A-Z][A-Z0-9_$#]*$')";
        cmd.CommandTimeout = 30;
        var missing = new List<string>();
        var count = 0;
        await using (var r = await cmd.ExecuteReaderAsync(cts.Token))
            while (await r.ReadAsync(cts.Token))
            {
                count++;
                var w = r.GetString(0);
                if (!Querithm.Intelligence.Oracle.OracleKeywords.IsReserved(w)) missing.Add(w);
            }
        Assert.True(count > 50, $"only {count} reserved words read");
        Assert.Empty(missing);
    }
}
