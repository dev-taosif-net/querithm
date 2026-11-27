using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.SqlServer;

/// <summary>
/// Server-level Linked Servers for SQL Server: lists sys.servers (is_linked = 1) locally, and lazily browses remote
/// catalogs (sp_catalogs), tables/views (sp_tables_ex) and tests connectivity (sp_testlinkedserver) on user demand.
/// Every server and catalog name is passed as a DbParameter, never interpolated into SQL.
/// </summary>
public sealed class SqlServerLinkedServers : ILinkedServerProvider
{
    public async Task<LinkedServersResult> ListServersAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT name, product, provider, data_source, catalog
            FROM sys.servers
            WHERE is_linked = 1
            ORDER BY name
            """;
        var list = new List<LinkedServerInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = Str(r, 0);
            if (string.IsNullOrEmpty(name)) continue;
            var product = Str(r, 1);
            var provider = Str(r, 2);
            var dataSource = Str(r, 3);
            var catalog = Str(r, 4);
            list.Add(new LinkedServerInfo
            {
                Name = name,
                Product = product,
                Provider = provider,
                DataSource = dataSource,
                Catalog = catalog,
                Note = FormatNote(product, provider, dataSource, catalog),
                CanBrowse = true,
                CanTest = true,
            });
        }
        return new LinkedServersResult { Servers = list.ToArray() };
    }

    internal const string DefaultCatalogDescription = "Default catalog (provider does not list catalogs)";

    public async Task<LinkedCatalogInfo[]> ListCatalogsAsync(DbConnection connection, string serverName, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXEC sys.sp_catalogs @server_name = @server";
        AddParam(cmd, "@server", serverName);
        var list = new List<LinkedCatalogInfo>();
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var nameOrd = Ordinal(r, "CATALOG_NAME", 0);
            var descOrd = Ordinal(r, "DESCRIPTION", 1);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var cat = Str(r, nameOrd);
                if (string.IsNullOrEmpty(cat)) continue;
                list.Add(new LinkedCatalogInfo { Name = cat, Description = descOrd < r.FieldCount ? Str(r, descOrd) : null });
            }
        }
        catch (SqlException ex) when (!ct.IsCancellationRequested && IsCatalogsUnsupported(ex))
        {
            return [new LinkedCatalogInfo { Name = "", Description = DefaultCatalogDescription }];
        }
        if (list.Count == 0)
            return [new LinkedCatalogInfo { Name = "", Description = DefaultCatalogDescription }];
        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return list.ToArray();
    }

    /// <summary>7311: OLE DB provider does not support DBSCHEMA_CATALOGS; 7301: provider lacks IDBSchemaRowset.</summary>
    internal static bool IsCatalogsUnsupported(SqlException ex) => ex.Number is 7311 or 7301;

    public async Task<LinkedTablesResult> ListTablesAsync(DbConnection connection, string serverName, string? catalog, int limit, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXEC sys.sp_tables_ex @table_server = @server, @table_catalog = @catalog";
        AddParam(cmd, "@server", serverName);
        AddParam(cmd, "@catalog", string.IsNullOrEmpty(catalog) ? DBNull.Value : catalog);
        var list = new List<LinkedTableInfo>();
        var truncated = false;
        var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        try
        {
            var schemaOrd = Ordinal(r, "TABLE_SCHEM", 1);
            var nameOrd = Ordinal(r, "TABLE_NAME", 2);
            var typeOrd = Ordinal(r, "TABLE_TYPE", 3);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var rawType = typeOrd < r.FieldCount ? Str(r, typeOrd) : null;
                var kind = rawType?.ToUpperInvariant() switch
                {
                    "TABLE" => LinkedTableKind.Table,
                    "VIEW" => LinkedTableKind.View,
                    _ => (LinkedTableKind?)null,
                };
                if (kind is null) continue;
                var name = nameOrd < r.FieldCount ? Str(r, nameOrd) : null;
                if (string.IsNullOrEmpty(name)) continue;
                var schema = schemaOrd < r.FieldCount ? (Str(r, schemaOrd) ?? "") : "";
                if (list.Count >= limit)
                {
                    truncated = true;
                    try { cmd.Cancel(); } catch { /* best-effort abort so reader dispose does not drain remaining TDS rows */ }
                    break;
                }
                list.Add(new LinkedTableInfo { Schema = schema, Name = name, Kind = kind.Value });
            }
        }
        finally
        {
            try { await r.DisposeAsync().ConfigureAwait(false); }
            catch (SqlException) when (truncated && !ct.IsCancellationRequested) { /* expected after cmd.Cancel() on truncation */ }
        }
        list.Sort((a, b) =>
        {
            var sc = StringComparer.OrdinalIgnoreCase.Compare(a.Schema, b.Schema);
            return sc != 0 ? sc : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        return new LinkedTablesResult
        {
            Tables = list.ToArray(),
            Truncated = truncated,
            Note = truncated ? $"Showing the first {limit:N0} remote tables and views." : null,
        };
    }

    public async Task<LinkedTestResult> TestAsync(DbConnection connection, string serverName, CancellationToken ct)
    {
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "EXEC sys.sp_testlinkedserver @servername = @server";
            AddParam(cmd, "@server", serverName);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return new LinkedTestResult { Ok = true, ElapsedMs = Math.Round(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 1) };
        }
        catch (SqlException ex) when (!ct.IsCancellationRequested)
        {
            return new LinkedTestResult
            {
                Ok = false,
                ElapsedMs = Math.Round(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 1),
                Error = ex.Message,
            };
        }
    }

    internal static string? FormatNote(string? product, string? provider, string? dataSource, string? catalog)
    {
        var parts = new List<string>(3);
        var kind = !string.IsNullOrWhiteSpace(product) ? product : provider;
        if (!string.IsNullOrWhiteSpace(kind)) parts.Add(kind);
        if (!string.IsNullOrWhiteSpace(dataSource) && !string.Equals(dataSource, kind, StringComparison.OrdinalIgnoreCase))
            parts.Add(dataSource);
        if (!string.IsNullOrWhiteSpace(catalog)) parts.Add(catalog);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim() is { Length: > 0 } s ? s : null;

    static int Ordinal(DbDataReader r, string name, int fallback)
    {
        for (var i = 0; i < r.FieldCount; i++)
            if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return i;
        return fallback;
    }

    static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
