using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Oracle;

/// <summary>
/// The part of Oracle's activity provider that every connection needs (D4 S03): its own session identity (§6 rule 7). The
/// SID comes from <c>SYS_CONTEXT('USERENV','SID')</c>, which needs no grant; SERIAL# and LOGON_TIME, which tell a reused
/// SID apart, come from V$SESSION when the account may read it. Without that grant the connection is registered
/// unidentified (§16): it serves queries, and kills are refused while it is open. The monitor panes arrive in D4 S12;
/// until then <see cref="Feature.Monitor"/> is refused, so nothing calls <see cref="ProbeAsync"/>.
/// </summary>
public sealed class OracleActivity : IActivityProvider
{
    internal const string SidSql = "SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'SID')) FROM DUAL";
    internal const string SessionSql = "SELECT SERIAL#, TO_CHAR(LOGON_TIME, 'YYYYMMDDHH24MISS') FROM V$SESSION WHERE SID = :sid";

    public const string NoSessionGrant =
        "This account cannot read V$SESSION, so the connection's server session cannot be told apart from a later one with the same SID.";

    public async Task<SessionKey> IdentityAsync(DbConnection connection, CancellationToken ct)
    {
        long sid;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = SidSql;
            sid = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        await using var q = (OracleCommand)connection.CreateCommand();
        q.CommandText = SessionSql;
        q.BindByName = true;
        q.Parameters.Add(new OracleParameter("sid", OracleDbType.Int64) { Value = sid });
        try
        {
            await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false)) throw new IdentityUnavailableException("V$SESSION does not show this connection's own session.");
            var serial = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            return new SessionKey { Id = sid, Instance = $"{serial.ToString(CultureInfo.InvariantCulture)}:{r.GetString(1)}" };
        }
        // ORA-00942 table or view does not exist (no grant); ORA-01031 insufficient privileges.
        catch (OracleException ex) when (ex.Number is 942 or 1031)
        {
            throw new IdentityUnavailableException(NoSessionGrant, ex);
        }
    }

    static readonly ConditionalWeakTable<DbConnection, string> Tokens = new();

    /// <summary>
    /// ODP.NET does not reconnect underneath a connection object (Application Continuity is off in the connection string), so
    /// the token is the object's own: a reopened connection is a new object.
    /// </summary>
    public string ConnectionToken(DbConnection connection) => Tokens.GetValue(connection, _ => Guid.NewGuid().ToString("N"));

    public string MonitorSessionSetupSql => "";

    public void PrepareMonitorConnection(DbConnection connection) { }

    /// <summary>ORA-01017 wrong user or password, 28000 account locked, 28001 password expired, 01005 empty password.</summary>
    public bool CredentialsRejected(Exception ex) => ex is OracleException o && OracleErrors.Code(o) is 1017 or 28000 or 28001 or 1005;

    public string WaitCategory(string waitType) => "Other";
    public bool IsBenignWait(string waitType) => false;

    /// <summary>"SERIAL#:YYYYMMDDHH24MISS".</summary>
    public bool IsValidInstance(string instance)
    {
        var colon = instance.IndexOf(':');
        return colon is > 0 and <= 10 && instance.Length == colon + 15 && instance.AsSpan(0, colon).ContainsOnlyDigits() && instance.AsSpan(colon + 1).ContainsOnlyDigits();
    }

    public bool IsValidStatementStart(string statementStart) => false;

    public Task<IActivitySession> ProbeAsync(DbConnection connection, CancellationToken ct) =>
        Task.FromException<IActivitySession>(new NotSupportedException(MonitorNotYet));

    public Task<(BlockedBySummary[]? Rows, string? Unavailable)> BlockedByAsync(DbConnection connection, long[] ids, CancellationToken ct) =>
        Task.FromResult<(BlockedBySummary[]?, string?)>((null, "Querithm cannot tell yet whether an Oracle session blocks others."));

    public const string MonitorNotYet = "The activity monitor is not available for Oracle yet.";
}

static class SpanDigits
{
    public static bool ContainsOnlyDigits(this ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (!char.IsAsciiDigit(c)) return false;
        return s.Length > 0;
    }
}
