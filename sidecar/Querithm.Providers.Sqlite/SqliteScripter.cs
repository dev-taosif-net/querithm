using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Sqlite;

namespace Querithm.Providers.Sqlite;

/// <summary>Script as CREATE / DROP / DROP and CREATE / SELECT from sqlite_schema's own SQL (Phase D1); identifiers quoted by the dialect.</summary>
public sealed class SqliteScripter : IObjectScripter
{
    public async Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, CancellationToken ct)
    {
        var e = (await SqliteMetadata.ReadSchemaAsync(connection, ct).ConfigureAwait(false))
                    .FirstOrDefault(x => string.Equals(x.Schema, obj.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, obj.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{obj.Schema}.{obj.Name} no longer exists.");
        var name = $"{SqliteTokenizer.Quote(e.Schema)}.{SqliteTokenizer.Quote(e.Name)}";
        var create = (e.Sql ?? "").TrimEnd().TrimEnd(';') + ";";
        var drop = $"DROP {e.Type.ToUpperInvariant()} {name};";
        return kind switch
        {
            ScriptKind.Create => create,
            ScriptKind.Drop => drop,
            ScriptKind.DropCreate => $"{drop}\n{create}",
            ScriptKind.SelectTop when e.Type is "table" or "view" => $"SELECT *\nFROM {name}\nLIMIT 100;",
            _ => throw new NotSupportedException($"SQLite has no {kind.ToString().ToUpperInvariant()} script for a {e.Type}."),
        };
    }
}
