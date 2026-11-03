using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Data.SqlClient;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.SqlServer;

/// <summary>
/// Deadlock history (B7) from the built-in system_health session: ring buffer plus event file. Strictly read-only —
/// never creates, alters or starts an event session (X15). XML is parsed with DTDs prohibited and no resolver (X4).
/// </summary>
public static class SqlServerDeadlocks
{
    public const int MaxEventChars = 5 * 1024 * 1024;

    internal static readonly XmlReaderSettings Safe = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
        // The ring buffer document as a whole; single events are bounded separately.
        MaxCharactersInDocument = 64L * 1024 * 1024,
    };

    const string SourcesSql = """
        SELECT COUNT(*) FROM sys.dm_xe_sessions WHERE name = N'system_health';
        SELECT CAST(c.column_value AS nvarchar(260)) FROM sys.dm_xe_session_object_columns c
        JOIN sys.dm_xe_sessions s ON s.address = c.event_session_address
        WHERE s.name = N'system_health' AND c.object_name = N'event_file' AND c.column_name = N'filename';
        SELECT CAST(t.target_data AS xml).value('(EventFileTarget/File/@name)[1]', 'nvarchar(260)') FROM sys.dm_xe_session_targets t
        JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address WHERE s.name = N'system_health' AND t.target_name = N'event_file';
        """;

    /// <summary>
    /// The ring buffer's whole XML (up to ~4 MB). Read only when the event files cannot be: they hold every event it holds and
    /// more. Reading it through SqlClient's async path allocated ~43 MB and took ~0.7 s per read on the fixture (2.1 M
    /// characters, ~2,000 pooled char buffers rented as its packets arrived), and the poll used to do that every 30 s per
    /// monitor even when the files had nothing new — the soak's heap swings were those pooled buffers.
    /// </summary>
    const string RingSql = """
        SELECT t.target_data FROM sys.dm_xe_session_targets t JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
        WHERE s.name = N'system_health' AND t.target_name = N'ring_buffer';
        """;

    /// <summary>The event-file read's own bound: its cost grows with the files' size (5.7 s for 30 MB on the fixture).</summary>
    public const int FileReadTimeoutSeconds = 8;

    /// <summary>
    /// Where the last poll stopped in the system_health files: the next poll reads only what came after it
    /// (fn_xe_file_target_read_file resumes after an initial file + offset). The first poll primes it with one full read.
    /// The ring buffer alone is not enough: its XML is capped at 4 MB and then leaves out the newest events (B0 row 15). It is
    /// read only when the files cannot be (see <see cref="RingSql"/>).
    /// </summary>
    public sealed class FileCursor
    {
        public string? File { get; internal set; }
        public long? Offset { get; internal set; }
        /// <summary>The priming read hit its time limit: polls use the ring buffer only (and say so).</summary>
        public bool GaveUp { get; internal set; }
        /// <summary>Priming reads that failed or were cut by the job's own time limit; two and the poll gives up on the files.</summary>
        public int PrimingFailures { get; internal set; }
    }

    /// <param name="cursor">The periodic poll's position (null: a full read, for the pane).</param>
    public static async Task<PaneResult<DeadlockEvent[]>> ReadAsync(DbConnection connection, long fromMs, long toMs, int max, CancellationToken ct, FileCursor? cursor = null)
    {
        var (running, ring, file, fileNote) = await ReadSourcesAsync(connection, fromMs, ct, cursor).ConfigureAwait(false);
        if (!running) return PaneResult<DeadlockEvent[]>.Degraded("The system_health event session is not running; SQL Server keeps no other deadlock history.");
        var events = new Dictionary<string, DeadlockEvent>();
        foreach (var e in Parse(ring, "ring buffer").Concat(file.SelectMany(x => Parse(x, "event file"))))
            if (e.At >= fromMs && e.At <= toMs) events.TryAdd(e.Id, e);
        var all = events.Values.OrderByDescending(e => e.At).ToList();
        var top = all.Take(max).ToArray();
        return all.Count > max ? PaneResult<DeadlockEvent[]>.Truncated(top, all.Count) : PaneResult<DeadlockEvent[]>.Ok(top, fileNote);
    }

    /// <summary>The original &lt;deadlock&gt; element of one event, for the SSMS-compatible .xdl export.</summary>
    public static async Task<string?> XmlAsync(DbConnection connection, string eventId, long fromMs, long toMs, CancellationToken ct)
    {
        var (_, ring, file, _) = await ReadSourcesAsync(connection, fromMs, ct, cursor: null).ConfigureAwait(false);
        foreach (var (raw, source) in new (string? Raw, string Source)[] { (ring, "ring buffer") }.Concat(file.Select(f => ((string?)f, "event file"))))
            foreach (var (graph, at, cut) in Graphs(raw))
                if (!cut && ParseGraph(graph, at, source, false).Id == eventId) return graph;
        return null;
    }

    static async Task<(bool Running, string? Ring, List<string> File, string? FileNote)> ReadSourcesAsync(DbConnection connection, long fromMs, CancellationToken ct, FileCursor? cursor)
    {
        bool running;
        string? ring = null, path = null, current = null;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = SourcesSql;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            running = r.GetInt32(0) > 0;
            await r.NextResultAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0)) path = r.GetString(0);
            if (await r.NextResultAsync(ct).ConfigureAwait(false) && await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0)) current = r.GetString(0);
        }
        var file = new List<string>();
        string? note = null;
        var filesRead = false;
        if (cursor?.GaveUp == true)
            note = $"The system_health event files are too large to read within {FileReadTimeoutSeconds} s; new deadlocks come from the ring buffer only, which may leave out the newest when it is full.";
        else if (running && path != null)
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                // "system_health.xel" → every rollover file "system_health*.xel" in the server's log directory. A poll resumes
                // after its cursor and reads the position of every event (not only deadlocks), so the next one starts there.
                var pattern = path.EndsWith(".xel", StringComparison.OrdinalIgnoreCase) ? path[..^4] + "*.xel" : path + "*.xel";
                cmd.CommandText = cursor == null
                    ? """
                      SELECT CASE WHEN DATALENGTH(event_data) > @maxBytes THEN NULL ELSE CAST(event_data AS nvarchar(max)) END, NULL, NULL
                      FROM sys.fn_xe_file_target_read_file(@path, NULL, NULL, NULL) WHERE object_name = N'xml_deadlock_report';
                      """
                    : """
                      SELECT CASE WHEN object_name <> N'xml_deadlock_report' THEN N'' WHEN DATALENGTH(event_data) > @maxBytes THEN NULL ELSE CAST(event_data AS nvarchar(max)) END,
                             file_name, file_offset
                      FROM sys.fn_xe_file_target_read_file(@path, NULL, @file, @offset);
                      """;
                // Priming needs only where the files end now, and the deadlocks since the monitor started: both are in the file
                // being written. Scanning every rollover file cost 0.9 s idle and 2.8–3.4 s under churn on the fixture (10,688
                // events), past the poll's limit on a busy host; the current file alone took 25 ms. A resumed read follows
                // rollovers, so it keeps the pattern.
                var priming = cursor is { File: null } && current != null;
                cmd.Parameters.Add(new SqlParameter("@path", System.Data.SqlDbType.NVarChar, 260) { Value = priming ? current : pattern });
                cmd.Parameters.Add(new SqlParameter("@maxBytes", System.Data.SqlDbType.BigInt) { Value = 2L * MaxEventChars });
                if (cursor != null)
                {
                    cmd.Parameters.Add(new SqlParameter("@file", System.Data.SqlDbType.NVarChar, 260) { Value = (object?)cursor.File ?? DBNull.Value });
                    cmd.Parameters.Add(new SqlParameter("@offset", System.Data.SqlDbType.BigInt) { Value = (object?)cursor.Offset ?? DBNull.Value });
                }
                cmd.CommandTimeout = FileReadTimeoutSeconds;
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var skipped = 0;
                string? lastFile = null;
                long? lastOffset = null;
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (!r.IsDBNull(1)) { lastFile = r.GetString(1); lastOffset = Convert.ToInt64(r.GetValue(2), System.Globalization.CultureInfo.InvariantCulture); }
                    if (r.IsDBNull(0)) { skipped++; continue; }
                    var xml = r.GetString(0);
                    if (xml.Length > 0) file.Add(xml);
                }
                if (cursor != null && lastFile != null) (cursor.File, cursor.Offset) = (lastFile, lastOffset);
                if (skipped > 0) note = $"{skipped} deadlock event(s) larger than 5 MB were skipped (event too large).";
                filesRead = true;
            }
            catch (DbException ex)
            {
                var timedOut = ex is SqlException { Number: -2 };
                if (cursor != null)
                {
                    // A priming read that cannot finish is not retried every poll: give up on the files at once on a timeout,
                    // after two failures otherwise. A resumed read that fails starts again (e.g. the file rolled over).
                    if (cursor.File == null) { if (timedOut || ++cursor.PrimingFailures >= 2) cursor.GaveUp = true; }
                    else (cursor.File, cursor.Offset) = (null, null);
                }
                note = timedOut
                    ? $"Reading the system_health event files took longer than {FileReadTimeoutSeconds} s (they are large); showing the ring buffer only."
                    : $"The system_health event file could not be read ({ex.Message.Split('\n')[0]}); showing the ring buffer only.";
            }
            catch (OperationCanceledException) when (cursor is { File: null })
            {
                // The poll job's own time limit cut the priming read (the job reports that): counted like a failure, so a
                // busy server is not asked for the same read every 30 s (soak: the deadlocks pane failed every poll).
                if (++cursor.PrimingFailures >= 2) cursor.GaveUp = true;
                throw;
            }
        }
        if (running && !filesRead)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = RingSql;
            ring = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        return (running, ring, file, note);
    }

    /// <summary>Every xml_deadlock_report in a ring-buffer document or event-file row, parsed independently (a cut-off event is kept as Partial).</summary>
    public static IEnumerable<DeadlockEvent> Parse(string? xml, string source)
    {
        foreach (var (graph, at, cut) in Graphs(xml))
        {
            if (graph.Length > MaxEventChars) continue; // event too large (X5)
            yield return ParseGraph(graph, at, source, cut);
        }
    }

    /// <summary>The &lt;deadlock&gt; element of each deadlock event with its timestamp; <c>Cut</c> = the input ended inside it.</summary>
    internal static IEnumerable<(string Graph, long At, bool Cut)> Graphs(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) yield break;
        var found = new List<(string, long, bool)>();
        try
        {
            using var x = XmlReader.Create(new StringReader(xml), Safe);
            long at = 0;
            var inDeadlockEvent = false;
            while (x.Read())
            {
                if (x.NodeType != XmlNodeType.Element) continue;
                if (x.LocalName == "event")
                {
                    inDeadlockEvent = x.GetAttribute("name") == "xml_deadlock_report";
                    at = ParseTime(x.GetAttribute("timestamp"));
                    continue;
                }
                if (!inDeadlockEvent || x.LocalName != "deadlock") continue;
                var sb = new StringBuilder();
                var cut = false;
                var ws = new XmlWriterSettings { ConformanceLevel = ConformanceLevel.Fragment, OmitXmlDeclaration = true };
                using (var w = XmlWriter.Create(sb, ws))
                {
                    try { w.WriteNode(x, true); }
                    catch (XmlException) { cut = true; }
                    // Disposing closes any element left open by a cut-off input, so what was read stays parseable.
                }
                found.Add((sb.ToString(), at, cut));
                inDeadlockEvent = false;
                if (cut) break;
            }
        }
        catch (XmlException) { /* truncated after the last complete event, or DTD refused (X4): keep what was read */ }
        foreach (var f in found) yield return f;
    }

    static long ParseTime(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeMilliseconds() : 0;

    sealed class Proc
    {
        public required string Id;
        public long? Spid;
        public string? Login, Host, App, Db, DbId, Iso, Mode, WaitResource, Text, Frame;
        public long? WaitMs;
    }

    /// <summary>Processes, resources and victims of one &lt;deadlock&gt; graph. Identifiers are kept as data, never markup.</summary>
    internal static DeadlockEvent ParseGraph(string graph, long at, string source, bool cut)
    {
        var victims = new List<string>();
        var procs = new List<Proc>();
        var resources = new List<DeadlockResource>();
        var partial = cut;
        try
        {
            using var x = XmlReader.Create(new StringReader(graph), Safe);
            Proc? current = null;
            string? resKind = null, resId = null, resDb = null, resObj = null, resIndex = null;
            var owners = new List<DeadlockResourceOwner>();
            var depth = -1;
            var section = "";
            while (x.Read())
            {
                if (x.NodeType == XmlNodeType.EndElement)
                {
                    if (section == "resource-list" && x.Depth == depth && resKind != null)
                    {
                        resources.Add(new DeadlockResource { Id = resId ?? $"r{resources.Count}", Kind = resKind, Database = resDb, Object = resObj, Index = resIndex, Owners = [.. owners] });
                        resKind = null;
                        owners.Clear();
                    }
                    continue;
                }
                if (x.NodeType != XmlNodeType.Element) continue;
                switch (x.LocalName)
                {
                    case "victim-list": case "process-list": case "resource-list": section = x.LocalName; depth = x.Depth + 1; continue;
                    case "victimProcess" when section == "victim-list": if (x.GetAttribute("id") is { } v) victims.Add(v); continue;
                    case "process" when section == "process-list":
                        current = new Proc
                        {
                            Id = x.GetAttribute("id") ?? $"p{procs.Count}",
                            Spid = long.TryParse(x.GetAttribute("spid"), NumberStyles.None, CultureInfo.InvariantCulture, out var sp) ? sp : null,
                            Login = x.GetAttribute("loginname"), Host = x.GetAttribute("hostname"), App = x.GetAttribute("clientapp"),
                            Db = x.GetAttribute("currentdbname"), DbId = x.GetAttribute("currentdb"), Iso = x.GetAttribute("isolationlevel"), Mode = x.GetAttribute("lockMode"),
                            WaitResource = x.GetAttribute("waitresource"),
                            WaitMs = long.TryParse(x.GetAttribute("waittime"), NumberStyles.None, CultureInfo.InvariantCulture, out var wt) ? wt : null,
                        };
                        procs.Add(current);
                        continue;
                    case "inputbuf" when current != null: current.Text = Preview(x.ReadElementContentAsString()); continue;
                    case "frame" when current != null && current.Frame == null:
                        var f = x.ReadElementContentAsString();
                        if (!string.IsNullOrWhiteSpace(f)) current.Frame = Preview(f.Trim());
                        continue;
                }
                if (section == "resource-list")
                {
                    if (x.Depth == depth)
                    {
                        resKind = x.LocalName;
                        resId = x.GetAttribute("id");
                        resDb = x.GetAttribute("dbid") is { } dbid ? procs.FirstOrDefault(p => p.DbId == dbid && p.Db != null)?.Db ?? $"db {dbid}" : null;
                        resObj = x.GetAttribute("objectname");
                        resIndex = x.GetAttribute("indexname");
                        if (x.IsEmptyElement)
                        {
                            resources.Add(new DeadlockResource { Id = resId ?? $"r{resources.Count}", Kind = resKind, Database = resDb, Object = resObj, Index = resIndex, Owners = [] });
                            resKind = null;
                        }
                    }
                    else if (resKind != null && x.LocalName is "owner" or "waiter" && x.GetAttribute("id") is { } pid)
                        owners.Add(new DeadlockResourceOwner { ProcessId = pid, Mode = x.GetAttribute("mode") ?? x.GetAttribute("requestType"), Role = x.LocalName });
                }
            }
        }
        catch (XmlException) { partial = true; }

        var processes = procs.Select(p => new DeadlockProcess
        {
            Id = p.Id, Spid = p.Spid, Login = p.Login, Host = p.Host, Program = p.App, Database = p.Db, IsolationLevel = p.Iso, LockMode = p.Mode,
            WaitResource = p.WaitResource, WaitMs = p.WaitMs, Text = p.Frame ?? p.Text, Victim = victims.Contains(p.Id),
        }).ToArray();
        return new DeadlockEvent
        {
            Id = GraphId(at, victims, processes.Select(p => p.Id), resources.Select(r => r.Id)),
            At = at, Victims = victims.Select(v => new DeadlockVictim { ProcessId = v }).ToArray(), Processes = processes, Resources = [.. resources],
            Database = processes.FirstOrDefault(p => p.Database != null)?.Database, Partial = partial, Source = source,
        };
    }

    /// <summary>Content identity of a graph: the same deadlock in the ring buffer and the event file gets the same id (X2).</summary>
    internal static string GraphId(long at, IEnumerable<string> victims, IEnumerable<string> processes, IEnumerable<string> resources)
    {
        var text = string.Join("|", at.ToString(CultureInfo.InvariantCulture), string.Join(",", victims.Order(StringComparer.Ordinal)),
            string.Join(",", processes.Order(StringComparer.Ordinal)), string.Join(",", resources.Order(StringComparer.Ordinal)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    }

    /// <summary>4 KB UTF-8 preview cut on a character boundary.</summary>
    internal static string Preview(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= 4096) return text;
        var n = 0;
        var i = 0;
        while (i < text.Length)
        {
            var len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var bytes = Encoding.UTF8.GetByteCount(text.AsSpan(i, len));
            if (n + bytes > 4096) break;
            n += bytes;
            i += len;
        }
        return text[..i];
    }
}
