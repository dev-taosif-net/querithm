using System.Data.Common;
using System.Globalization;
using MySqlConnector;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.MySql;

/// <summary>
/// Server-level external servers defined via CREATE SERVER (mysql.servers). Explicitly omits the Password column so
/// credentials never leave the database server, and degrades cleanly when the login lacks SELECT on mysql.servers.
/// </summary>
public sealed class MySqlLinkedServers : ILinkedServerProvider
{
    public const string DeniedNote = "This login cannot read mysql.servers (SELECT privilege on mysql.servers is required).";

    public async Task<LinkedServersResult> ListServersAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        // Never select the Password column (§13 rule 1).
        cmd.CommandText = """
            SELECT Server_name, Host, Db, Username, Port, Socket, Wrapper, Owner
            FROM mysql.servers
            ORDER BY Server_name
            """;
        var list = new List<LinkedServerInfo>();
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = Str(r, 0);
                if (string.IsNullOrEmpty(name)) continue;
                var host = Str(r, 1);
                var db = Str(r, 2);
                var port = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4), CultureInfo.InvariantCulture);
                var socket = Str(r, 5);
                var wrapper = Str(r, 6);
                var endpoint = FormatEndpoint(host, port, db, socket);
                list.Add(new LinkedServerInfo
                {
                    Name = name,
                    Product = wrapper,
                    Provider = wrapper,
                    DataSource = endpoint,
                    Catalog = db,
                    Note = FormatNote(wrapper, endpoint),
                    CanBrowse = false,
                    CanTest = false,
                });
            }
            return new LinkedServersResult { Servers = list.ToArray() };
        }
        catch (MySqlException ex) when (IsAccessDenied(ex))
        {
            return new LinkedServersResult { Servers = [], Note = DeniedNote };
        }
    }

    /// <summary>1142 ER_TABLEACCESS_DENIED_ERROR, 1044 ER_DBACCESS_DENIED_ERROR, 1146 ER_NO_SUCH_TABLE.</summary>
    internal static bool IsAccessDenied(MySqlException ex) => ex.Number is 1142 or 1044 or 1146;

    internal static string? FormatEndpoint(string? host, int port, string? db, string? socket)
    {
        var target = !string.IsNullOrWhiteSpace(host)
            ? (port > 0 ? $"{host}:{port.ToString(CultureInfo.InvariantCulture)}" : host)
            : socket;
        if (string.IsNullOrWhiteSpace(target)) return db;
        return string.IsNullOrWhiteSpace(db) ? target : $"{target}/{db}";
    }

    internal static string? FormatNote(string? wrapper, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(wrapper)) return endpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) return wrapper;
        return $"{wrapper} · {endpoint}";
    }

    static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim() is { Length: > 0 } s ? s : null;
}
