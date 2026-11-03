using MySqlConnector;
using Querithm.Contract;
using static Querithm.Tests.Integration.MySqlIntegrationTests;

namespace Querithm.Tests.Integration;

/// <summary>
/// Phase D2 identifier case rules (QUERITHM_IT=1). lower_case_table_names is fixed when a server is initialized: the
/// default fixture (Linux, 0) keeps Sales and sales apart, the separately initialized container (1) stores names in lower
/// case. Distinct objects never share an identity, and a name typed in another case still finds its table where the
/// server folds case.
/// </summary>
public sealed class MySqlCaseTests
{
    static async Task ExecAsync(string product, string sql, string? database = null)
    {
        await EnsureDatabaseAsync(product);
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port(product)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False;Database={database ?? Db(product)}");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task<SchemaSnapshot> SnapshotAsync(MySqlIntegrationTests.Host h, Profile p, string sid, string database, Func<SchemaSnapshot, bool> ready)
    {
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = database, SessionId = sid, Full = true });
        var deadline = DateTime.UtcNow.AddSeconds(15);
        SchemaSnapshot snap;
        do
        {
            await Task.Delay(50);
            snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = database, SessionId = sid });
        } while (!ready(snap) && DateTime.UtcNow < deadline);
        return snap;
    }

    [IntegrationFact]
    public async Task A_case_sensitive_server_keeps_sales_and_Sales_apart()
    {
        const string product = "mysql";
        await ExecAsync(product, """
            DROP DATABASE IF EXISTS q_case_Sales; DROP DATABASE IF EXISTS q_case_sales;
            CREATE DATABASE q_case_Sales; CREATE DATABASE q_case_sales;
            CREATE TABLE q_case_Sales.T (id INT PRIMARY KEY, v VARCHAR(5)); CREATE TABLE q_case_Sales.t (id INT PRIMARY KEY, v VARCHAR(5));
            INSERT INTO q_case_Sales.T VALUES (1, 'upper'); INSERT INTO q_case_Sales.t VALUES (1, 'lower');
            """);
        var (h, p, sid) = await OpenAsync(product);
        await using var _ = h;
        var dbs = (await h.Rpc.MetaDatabases(new SessionRef { SessionId = sid })).Select(d => d.Name).ToArray();
        Assert.Contains("q_case_Sales", dbs);
        Assert.Contains("q_case_sales", dbs);
        await h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = "q_case_Sales" });
        var snap = await SnapshotAsync(h, p, sid, "q_case_Sales", s => s.Objects.Length >= 2);
        var upper = snap.Objects.Single(o => o.Name == "T");
        var lower = snap.Objects.Single(o => o.Name == "t");
        Assert.NotEqual(upper.Id, lower.Id);
        // A result from T edits T, never t.
        var (done, _, exec) = await Run(h, sid, "SELECT id, v FROM T");
        Assert.Null(done.Error);
        var edit = h.Notifier.Of<QueryResultSetEvent>().Single(e => e.ExecutionId == exec).Edit!;
        Assert.True(edit.Editable, edit.Reason);
        Assert.Equal((upper.Id, "T"), (edit.ObjectId, edit.Table));
    }

    [IntegrationFact]
    public async Task A_case_insensitive_server_folds_names_and_a_differently_cased_query_still_edits_its_table()
    {
        const string product = "mysql-lctn";
        await ExecAsync(product, "DROP TABLE IF EXISTS Orders; CREATE TABLE Orders (Id INT PRIMARY KEY, Name VARCHAR(10)); INSERT INTO Orders VALUES (1, 'a');");
        // The same name in another case is the same table here.
        var dup = await Assert.ThrowsAsync<MySqlException>(() => ExecAsync(product, "CREATE TABLE ORDERS (x INT)"));
        Assert.Equal(MySqlErrorCode.TableExists, dup.ErrorCode);
        var (h, p, sid) = await OpenAsync(product);
        await using var _ = h;
        var snap = await SnapshotAsync(h, p, sid, Db(product), s => s.Objects.Any(o => o.Name == "orders"));
        Assert.DoesNotContain(snap.Objects, o => o.Name == "Orders");
        var (done, _, exec) = await Run(h, sid, "SELECT ID, NAME FROM ORDERS");
        Assert.Null(done.Error);
        var edit = h.Notifier.Of<QueryResultSetEvent>().Single(e => e.ExecutionId == exec).Edit!;
        Assert.True(edit.Editable, edit.Reason);
        Assert.Equal("orders", edit.Table);
        Assert.Equal(["Id"], edit.KeyColumns); // column names keep their declared case
        var r = await h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = exec,
            Updates = [new GridRowUpdate { RowIndex = 0, Key = [new GridValue { Text = "1" }], Changes = new() { ["Name"] = new GridValue { Text = "b" } }, Original = new() { ["Name"] = new GridValue { Text = "a" } } }],
        });
        Assert.True(r.Ok, r.Error);
    }
}
