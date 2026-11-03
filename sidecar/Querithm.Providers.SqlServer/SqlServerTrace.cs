using System.Data.Common;
using System.Globalization;
using System.Xml.Linq;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.SqlServer;

/// <summary>Extended Events trace on one SPID with a ring_buffer target (§11). Never file targets.</summary>
public sealed class SqlServerTrace : ITraceProvider
{
    static readonly string[] Events = ["sqlserver.sp_statement_starting", "sqlserver.sp_statement_completed", "sqlserver.sql_statement_completed", "sqlserver.error_reported", "sqlserver.module_end"];

    static async Task<int> EngineEditionAsync(DbConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>Azure SQL Database (EngineEdition 5) uses database-scoped sessions.</summary>
    static string Scope(int edition) => edition == 5 ? "DATABASE" : "SERVER";

    public async Task<CapabilityInfo> CheckAsync(DbConnection c, CancellationToken ct)
    {
        var edition = await EngineEditionAsync(c, ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = edition == 5
            ? "SELECT HAS_PERMS_BY_NAME(NULL, 'DATABASE', 'ALTER ANY DATABASE EVENT SESSION')"
            : "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY EVENT SESSION')";
        var ok = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0, CultureInfo.InvariantCulture) == 1;
        return ok
            ? new CapabilityInfo { Available = true }
            : new CapabilityInfo { Available = false, Reason = edition == 5 ? "Trace needs ALTER ANY DATABASE EVENT SESSION." : "Trace needs the ALTER ANY EVENT SESSION permission." };
    }

    public async Task StartAsync(DbConnection c, string name, int spid, CancellationToken ct)
    {
        var scope = Scope(await EngineEditionAsync(c, ct).ConfigureAwait(false));
        var q = "[" + name.Replace("]", "]]") + "]";
        var events = string.Join(",\n", Events.Select(e =>
            $"ADD EVENT {e}(ACTION(package0.event_sequence, sqlserver.session_id) WHERE (sqlserver.session_id = {spid}))"));
        await Exec(c, $"""
            IF EXISTS (SELECT 1 FROM sys.{(scope == "SERVER" ? "server" : "database")}_event_sessions WHERE name = N'{name.Replace("'", "''")}')
                DROP EVENT SESSION {q} ON {scope};
            CREATE EVENT SESSION {q} ON {scope}
            {events}
            ADD TARGET package0.ring_buffer(SET max_memory = 4096)
            WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS, STARTUP_STATE = OFF);
            ALTER EVENT SESSION {q} ON {scope} STATE = START;
            """, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TraceEvent>> PollAsync(DbConnection c, string name, long afterSeq, CancellationToken ct)
    {
        var azure = await EngineEditionAsync(c, ct).ConfigureAwait(false) == 5;
        await using var cmd = c.CreateCommand();
        cmd.CommandText = azure
            ? "SELECT CAST(t.target_data AS nvarchar(max)) FROM sys.dm_xe_database_session_targets t JOIN sys.dm_xe_database_sessions s ON s.address = t.event_session_address WHERE s.name = @name AND t.target_name = N'ring_buffer'"
            : "SELECT CAST(t.target_data AS nvarchar(max)) FROM sys.dm_xe_session_targets t JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address WHERE s.name = @name AND t.target_name = N'ring_buffer'";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = name;
        cmd.Parameters.Add(p);
        var xml = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (xml == null) return [];
        var events = ParseRingBuffer(xml, afterSeq);
        // object_name is often empty for sp_statement_* events: resolve object ids to names on the server.
        var ids = events.Where(e => e.ProcName == null && e.ObjectId is > 0).Select(e => e.ObjectId!.Value).Distinct().ToList();
        if (ids.Count > 0)
        {
            await using var q = c.CreateCommand();
            q.CommandText = $"SELECT o.object_id, s.name + N'.' + o.name FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE o.object_id IN ({string.Join(",", ids)})";
            var names = new Dictionary<long, string>();
            await using (var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false)) names[r.GetInt32(0)] = r.GetString(1);
            for (var i = 0; i < events.Count; i++)
                if (events[i].ProcName == null && events[i].ObjectId is { } oid && names.TryGetValue(oid, out var n))
                    events[i] = events[i] with { ProcName = n };
        }
        return events;
    }

    public static List<TraceEvent> ParseRingBuffer(string xml, long afterSeq)
    {
        var list = new List<TraceEvent>();
        foreach (var e in XDocument.Parse(xml).Root!.Elements("event"))
        {
            string? Data(string n) => e.Elements("data").FirstOrDefault(d => (string?)d.Attribute("name") == n)?.Element("value")?.Value;
            string? Action(string n) => e.Elements("action").FirstOrDefault(d => (string?)d.Attribute("name") == n)?.Element("value")?.Value;
            long? L(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
            var seq = L(Action("event_sequence")) ?? 0;
            if (seq <= afterSeq) continue;
            var ts = DateTimeOffset.TryParse((string?)e.Attribute("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.UtcNow;
            var name = (string?)e.Attribute("name") ?? "event";
            list.Add(new TraceEvent
            {
                Seq = seq,
                Ts = ts.ToUnixTimeMilliseconds() * 1000 + ts.Ticks % TimeSpan.TicksPerMillisecond / 10,
                EventName = name,
                ProcName = Data("object_name") is { Length: > 0 } on ? on : null,
                ObjectId = L(Data("object_id")),
                Line = (int?)L(Data("line_number")),
                OffsetStart = (int?)L(Data("offset")),
                OffsetEnd = (int?)L(Data("offset_end")),
                DurationUs = L(Data("duration")),
                RowCount = L(Data("row_count")),
                Statement = Data("statement"),
                Error = name == "error_reported" ? $"{Data("error_number")}: {Data("message")}" : null,
            });
        }
        return list.OrderBy(x => x.Seq).ToList();
    }

    public async Task StopAsync(DbConnection c, string name, CancellationToken ct)
    {
        var scope = Scope(await EngineEditionAsync(c, ct).ConfigureAwait(false));
        await Exec(c, $"""
            IF EXISTS (SELECT 1 FROM sys.{(scope == "SERVER" ? "server" : "database")}_event_sessions WHERE name = N'{name.Replace("'", "''")}')
                DROP EVENT SESSION [{name.Replace("]", "]]")}] ON {scope};
            """, ct).ConfigureAwait(false);
    }

    public async Task DropOrphansAsync(DbConnection c, string prefix, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.server_event_sessions WHERE name LIKE @p";
        var p = cmd.CreateParameter();
        p.ParameterName = "@p";
        p.Value = prefix.Replace("_", "[_]") + "%";
        cmd.Parameters.Add(p);
        var names = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await r.ReadAsync(ct).ConfigureAwait(false)) names.Add(r.GetString(0));
        foreach (var n in names) await StopAsync(c, n, ct).ConfigureAwait(false);
    }

    static async Task Exec(DbConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
