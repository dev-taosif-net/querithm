using System.Diagnostics;
using System.Text;
using Apache.Arrow;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Tests.Integration;

/// <summary>Import wizard end to end on both fixture engines.</summary>
[Collection("dataedit")]
public sealed class ImportIntegrationTests : IDisposable
{
    const string Db = "querithm_edit";
    readonly string _dir = Directory.CreateTempSubdirectory("qimport-it-").FullName;

    public static TheoryData<string> Engines => TestEngines.Names;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    static async Task<(TestHost Host, Profile Profile, string Session)> OpenAsync(string engine, EnvironmentKind env = EnvironmentKind.Dev)
    {
        var h = new TestHost();
        if (engine == "mssql")
        {
            await h.EnsureDatabaseAsync(Engine.Mssql, Db);
            var (p, sid) = await h.OpenMssqlAsync(Db, env);
            return (h, p, sid);
        }
        await h.EnsureDatabaseAsync(Engine.Pg, Db);
        var prof = await h.Rpc.ProfileSave(new Profile
        {
            Id = "", Name = "Fixture PG import", Engine = Engine.Pg, Host = "localhost", Port = 54329, Auth = AuthKind.Password, Username = "postgres",
            Environment = env, TlsMode = TlsMode.Prefer, DatabaseDefault = Db,
        });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = prof.Id, Secret = TestHost.PgPassword, Database = Db });
        return (h, prof, s.SessionId);
    }

    static string Schema(string engine) => engine == "mssql" ? "dbo" : "public";

    static async Task Ok(TestHost h, string sid, string sql, bool prod = false)
    {
        var (done, _, id) = await h.RunAsync(sid, sql, prod ? new ExecuteOptions { ConfirmedProd = true, ConfirmedDestructive = true } : null);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
    }

    static async Task<long> CountAsync(TestHost h, string sid, string engine, string table)
    {
        var (done, b, id) = await h.RunAsync(sid, engine == "pg" ? $"SELECT count(*)::bigint FROM {table}" : $"SELECT CAST(count(*) AS bigint) FROM {table}");
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        return ((Int64Array)b[0].Column(0)).GetValue(0)!.Value;
    }

    static async Task RefreshAsync(TestHost h, Profile p)
    {
        h.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = Db, Full = true });
        await Task.Delay(100);
        await h.Services.Metadata.WaitAsync(p.Id, Db);
    }

    static async Task<ImportProgressEvent> WaitImportAsync(TestHost h, string jobId, int timeoutMs = 120_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var done = h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == jobId && e.Done);
            if (done != null) return done;
            await Task.Delay(25);
        }
        throw new TimeoutException($"import {jobId} did not finish");
    }

    static string Csv(int rows, Func<int, string>? special = null)
    {
        var sb = new StringBuilder("id,name,qty,price,ok,born,at,code,note,ratio\n");
        for (var i = 1; i <= rows; i++)
            sb.Append(special?.Invoke(i) ?? $"{i},name {i},{i % 100},{i % 1000}.25,{(i % 2 == 0 ? "true" : "false")},2026-01-{1 + i % 28:00},2026-10-21 10:{i % 60:00}:00,C{i:000000},\"note, {i}\",0.{i % 10}").Append('\n');
        return sb.ToString();
    }

    async Task<(ImportInspectResult Ins, ImportTargetResult Target)> PrepareNewTableAsync(TestHost h, string sid, string engine, string file, string table)
    {
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        var target = await h.Rpc.ImportCheckTarget(new ImportTargetParams { SessionId = sid, InspectionId = ins.InspectionId, Schema = Schema(engine), Table = table, NewTable = true, PrimaryKey = ["id"] });
        await Ok(h, sid, target.CreateTableSql!);
        return (ins, target);
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Hundred_thousand_rows_load_into_a_new_table(string engine)
    {
        var (h, p, sid) = await OpenAsync(engine);
        await using var _h = h;
        await Ok(h, sid, $"DROP TABLE IF EXISTS {Schema(engine)}.q_import_big");
        var file = Write("big.csv", Csv(100_000));
        var (ins, target) = await PrepareNewTableAsync(h, sid, engine, file, "q_import_big");
        Assert.Equal(100_000, ins.RowCount);
        Assert.Equal(10, ins.Columns.Length);
        Assert.Contains(engine == "pg" ? "numeric(" : "decimal(", target.CreateTableSql);
        var sw = Stopwatch.StartNew();
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_big" });
        var done = await WaitImportAsync(h, run.JobId!);
        Console.WriteLine($"[metric 23] {engine}: 100k x 10 CSV in {sw.ElapsedMilliseconds} ms");
        Assert.Null(done.Error);
        Assert.Equal((100_000L, 0L), (done.RowsLoaded, done.Rejected));
        Assert.Equal(100_000, await CountAsync(h, sid, engine, $"{Schema(engine)}.q_import_big"));
        Assert.Contains(h.Services.History.List(new HistoryListParams { Limit = 20 }), e => e.Sql.StartsWith("-- Import big.csv"));
        _ = p;
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Bad_rows_go_to_the_reject_log_up_to_the_limit(string engine)
    {
        var (h, p, sid) = await OpenAsync(engine);
        await using var _h = h;
        var t = $"{Schema(engine)}.q_import_rej";
        await Ok(h, sid, $"DROP TABLE IF EXISTS {t}; CREATE TABLE {t} (id int PRIMARY KEY, qty int NULL, tag varchar(5) NULL)");
        await RefreshAsync(h, p);
        var file = Write("rej.csv", "id,qty,tag\n1,10,a\n2,abc,b\n3,30,toolongtag\n4,40,d\n");
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        var check = await h.Rpc.ImportCheckTarget(new ImportTargetParams { SessionId = sid, InspectionId = ins.InspectionId, Schema = Schema(engine), Table = "q_import_rej" });
        Assert.Contains(check.Verdicts, v => v.TargetColumn == "qty" && v.Kind == ImportVerdictKind.Mismatch);
        Assert.Contains(check.Verdicts, v => v.TargetColumn == "tag" && v.Kind == ImportVerdictKind.Widen && v.SuggestedSql != null);

        // Default: stop at the first bad row and import nothing.
        var strict = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_rej" });
        var s = await WaitImportAsync(h, strict.JobId!);
        Assert.Contains("Row 2", s.Error);
        Assert.Equal(0, await CountAsync(h, sid, engine, t));

        var ins2 = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        var lax = await h.Rpc.ImportRun(new ImportRunParams
        {
            SessionId = sid, InspectionId = ins2.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_rej",
            Options = new ImportRunOptions { MaxRejects = 5 },
        });
        var l = await WaitImportAsync(h, lax.JobId!);
        Assert.Null(l.Error);
        Assert.Equal((2L, 2L), (l.RowsLoaded, l.Rejected));
        Assert.Equal(2, await CountAsync(h, sid, engine, t));
        var log = await File.ReadAllLinesAsync(l.RejectFile!);
        Assert.Equal("row,reason,id,qty,tag", log[0]);
        Assert.StartsWith("2,", log[1]);
        Assert.Contains("qty", log[1]);
        Assert.StartsWith("3,", log[2]);
        Assert.Contains("tag", log[2]);
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Batches_mode_keeps_committed_batches_and_reports_where_to_resume(string engine)
    {
        var (h, p, sid) = await OpenAsync(engine);
        await using var _h = h;
        var t = $"{Schema(engine)}.q_import_batch";
        await Ok(h, sid, $"DROP TABLE IF EXISTS {t}; CREATE TABLE {t} (id int PRIMARY KEY, name varchar(20) NULL)");
        await RefreshAsync(h, p);
        // Row 2,500 repeats a key: the server rejects that batch; the first two batches stay committed.
        var sb = new StringBuilder("id,name\n");
        for (var i = 1; i <= 3000; i++) sb.Append(i == 2500 ? 1 : i).Append(",n").Append(i).Append('\n');
        var file = Write("batch.csv", sb.ToString());
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        var run = await h.Rpc.ImportRun(new ImportRunParams
        {
            SessionId = sid, InspectionId = ins.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_batch",
            Options = new ImportRunOptions { TransactionMode = ImportTransactionMode.Batches, CommitEvery = 1000 },
        });
        var done = await WaitImportAsync(h, run.JobId!);
        Assert.NotNull(done.Error);
        Assert.Equal(2000L, done.CommittedRows);
        Assert.Equal(2000, await CountAsync(h, sid, engine, t));
        // Batches mode refuses to run inside an open transaction (it would commit it).
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var ins2 = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.ImportRun(new ImportRunParams
        {
            SessionId = sid, InspectionId = ins2.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_batch",
            Options = new ImportRunOptions { TransactionMode = ImportTransactionMode.Batches },
        }));
        await h.Rpc.TxRollback(new SessionRef { SessionId = sid });
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task An_import_inside_an_open_transaction_rolls_back_with_it(string engine)
    {
        var (h, p, sid) = await OpenAsync(engine);
        await using var _h = h;
        var t = $"{Schema(engine)}.q_import_tx";
        await Ok(h, sid, $"DROP TABLE IF EXISTS {t}; CREATE TABLE {t} (id int PRIMARY KEY, name varchar(20) NULL)");
        await RefreshAsync(h, p);
        var file = Write("tx.csv", "id,name\n1,a\n2,b\n3,c\n");
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_tx" });
        var done = await WaitImportAsync(h, run.JobId!);
        Assert.Null(done.Error);
        Assert.Equal(3, await CountAsync(h, sid, engine, t)); // visible inside the transaction
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        await h.Rpc.TxRollback(new SessionRef { SessionId = sid });
        Assert.Equal(0, await CountAsync(h, sid, engine, t));
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Cancel_rolls_the_single_transaction_back(string engine)
    {
        var (h, p, sid) = await OpenAsync(engine);
        await using var _h = h;
        await Ok(h, sid, $"DROP TABLE IF EXISTS {Schema(engine)}.q_import_cancel");
        var file = Write("cancel.csv", Csv(400_000));
        var (ins, _) = await PrepareNewTableAsync(h, sid, engine, file, "q_import_cancel");
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_cancel" });
        await Task.Delay(300);
        Assert.True((await h.Rpc.ImportCancel(new ImportJobRef { JobId = run.JobId! })).Cancelled);
        var done = await WaitImportAsync(h, run.JobId!);
        Assert.True(done.Cancelled);
        Assert.Equal(0, await CountAsync(h, sid, engine, $"{Schema(engine)}.q_import_cancel"));
        _ = p;
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Imports_on_prod_are_confirmed_every_time(string engine)
    {
        var (h, _, sid) = await OpenAsync(engine, EnvironmentKind.Prod);
        await using var _h = h;
        await Ok(h, sid, $"DROP TABLE IF EXISTS {Schema(engine)}.q_import_prod; CREATE TABLE {Schema(engine)}.q_import_prod (id int PRIMARY KEY)", prod: true);
        var p = (await h.Rpc.ProfileList()).Single(x => x.Environment == EnvironmentKind.Prod);
        await RefreshAsync(h, p);
        var file = Write("prod.csv", "id\n1\n2\n");
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        var blocked = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_prod" });
        Assert.Equal(GuardReason.ProdWrite, blocked.Blocked?.Reason);
        Assert.Contains("2 row(s)", blocked.Blocked!.Findings[0].Statement);
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_prod", ConfirmedProd = true });
        Assert.Null((await WaitImportAsync(h, run.JobId!)).Error);
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Json_files_load_with_name_mapping(string engine)
    {
        var (h, p, sid) = await OpenAsync(engine);
        await using var _h = h;
        var t = $"{Schema(engine)}.q_import_json";
        await Ok(h, sid, $"DROP TABLE IF EXISTS {t}; CREATE TABLE {t} (order_id int PRIMARY KEY, customer_name varchar(50) NOT NULL, placed date NULL, total decimal(10,2) NULL)");
        await RefreshAsync(h, p);
        var file = Write("o.json", """[{"Order ID":1,"Customer Name":"Acme","placed":"2026-10-21","total":12.5},{"Order ID":2,"Customer Name":"Bolt","total":null,"ignored":"x"}]""");
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        var check = await h.Rpc.ImportCheckTarget(new ImportTargetParams { SessionId = sid, InspectionId = ins.InspectionId, Schema = Schema(engine), Table = "q_import_json" });
        Assert.Equal(["order_id", "customer_name", "placed", "total", null], check.Mapping.Select(m => m.TargetColumn));
        Assert.Contains(check.Verdicts, v => v.FileColumn == "ignored" && v.Kind == ImportVerdictKind.MissingInTarget);
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_json" });
        var done = await WaitImportAsync(h, run.JobId!);
        Assert.Null(done.Error);
        Assert.Equal(2, await CountAsync(h, sid, engine, t));
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Target_check_flags_required_and_not_null_columns(string engine)
    {
        var (h, p, sid) = await OpenAsync(engine);
        await using var _h = h;
        var t = $"{Schema(engine)}.q_import_chk";
        await Ok(h, sid, $"DROP TABLE IF EXISTS {t}; CREATE TABLE {t} (id int PRIMARY KEY, name varchar(10) NOT NULL, region varchar(10) NOT NULL)");
        await RefreshAsync(h, p);
        var file = Write("chk.csv", "id,name\n1,a\n2,\n");
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        var check = await h.Rpc.ImportCheckTarget(new ImportTargetParams { SessionId = sid, InspectionId = ins.InspectionId, Schema = Schema(engine), Table = "q_import_chk" });
        Assert.Contains(check.Verdicts, v => v.TargetColumn == "name" && v.Kind == ImportVerdictKind.NullIntoNotNull && v.Message.Contains("row 2"));
        Assert.Contains(check.Verdicts, v => v.TargetColumn == "region" && v.Kind == ImportVerdictKind.RequiredUnmapped);
        await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.ImportCheckTarget(new ImportTargetParams { SessionId = sid, InspectionId = ins.InspectionId, Schema = Schema(engine), Table = "q_import_chk", NewTable = true }));
    }

    sealed class ListRows(List<string?[]> rows) : IBulkRows
    {
        int _i = -1;
        public bool Read() => ++_i < rows.Count;
        public IReadOnlyList<string?> Current => rows[_i];
        public long Row => _i + 1;
        public List<string> Rejected { get; } = [];
        public void Reject(string reason) => Rejected.Add($"{Row}: {reason}");
        public long LoadedRows { get; private set; }
        public void Loaded(long totalRows, bool committed) => LoadedRows = totalRows;
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task The_generic_insert_loader_works_on_every_engine(string engine)
    {
        var (h, p, sid) = await OpenAsync(engine);
        await using var _h = h;
        var t = $"{Schema(engine)}.q_import_generic";
        await Ok(h, sid, $"DROP TABLE IF EXISTS {t}; CREATE TABLE {t} (id int PRIMARY KEY, amount decimal(10,2) NULL, at date NULL)");
        var provider = h.Services.Sessions.Get(sid).Provider;
        await using var conn = await provider.OpenAsync(p, engine == "pg" ? TestHost.PgPassword : TestHost.MssqlPassword, Db, null, CancellationToken.None);
        var data = Enumerable.Range(1, 1200).Select(i => new string?[] { i.ToString(), i == 7 && engine == "mssql" ? "x7" : $"{i}.50", i % 3 == 0 ? null : "2026-10-21" }).ToList();
        var rows = new ListRows(data);
        var target = new BulkTarget(Schema(engine), "q_import_generic", [new BulkColumn("id", engine == "pg" ? "integer" : "int"), new BulkColumn("amount", engine == "pg" ? "numeric(10,2)" : "decimal(10,2)"), new BulkColumn("at", "date")]);
        await new BatchInsertLoader(provider.DataEdit, provider.Transactions).LoadAsync(conn, target, rows, new ImportRunOptions(), new BulkTransaction(BulkTxMode.Own), null, CancellationToken.None);
        // SQL Server converts in the sidecar and rejects row 7; PostgreSQL leaves conversion to the server.
        Assert.Equal(engine == "mssql" ? 1 : 0, rows.Rejected.Count);
        Assert.Equal(engine == "mssql" ? 1199 : 1200, await CountAsync(h, sid, engine, t));
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Kept_identity_values_load_inside_an_open_transaction(string engine)
    {
        var (h, p, sid) = await OpenAsync(engine);
        await using var _h = h;
        var t = $"{Schema(engine)}.q_import_ident";
        var id = engine == "mssql" ? "id int IDENTITY(1,1) PRIMARY KEY" : "id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY";
        await Ok(h, sid, $"DROP TABLE IF EXISTS {t}; CREATE TABLE {t} ({id}, name varchar(20) NULL)");
        await RefreshAsync(h, p);
        var file = Write("ident.csv", "id,name\n101,a\n205,b\n");
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        var run = await h.Rpc.ImportRun(new ImportRunParams
        {
            SessionId = sid, InspectionId = ins.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_ident",
            Options = new ImportRunOptions { KeepIdentity = true },
        });
        var done = await WaitImportAsync(h, run.JobId!);
        Assert.Null(done.Error);
        var (ok, b, eid) = await h.RunAsync(sid, $"SELECT CAST(sum(id) AS bigint) FROM {t}");
        Assert.True(ok.State == ExecState.Done, string.Join("\n", h.Messages(eid)));
        Assert.Equal(306L, ((Int64Array)b[0].Column(0)).GetValue(0));
        if (engine == "mssql") await Ok(h, sid, $"INSERT INTO {t} (name) VALUES ('c')"); // IDENTITY_INSERT is off again
        await h.Rpc.TxRollback(new SessionRef { SessionId = sid });
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Blank_cells_load_as_null_into_typed_columns(string engine)
    {
        var (h, p, sid) = await OpenAsync(engine);
        await using var _h = h;
        var t = $"{Schema(engine)}.q_import_blank";
        await Ok(h, sid, $"DROP TABLE IF EXISTS {t}; CREATE TABLE {t} (id int PRIMARY KEY, qty int NULL, born date NULL, note varchar(10) NULL)");
        await RefreshAsync(h, p);
        var file = Write("blank.csv", "id,qty,born,note\n1,   ,  ,  \n2,5,2026-01-02,x\n");
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = file, FilePath = file });
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = file, FilePath = file, Schema = Schema(engine), Table = "q_import_blank" });
        var done = await WaitImportAsync(h, run.JobId!);
        Assert.Null(done.Error);
        Assert.Equal((2L, 0L), (done.RowsLoaded, done.Rejected));
        var (ok, b, eid) = await h.RunAsync(sid, $"SELECT CAST(count(*) AS bigint) FROM {t} WHERE id = 1 AND qty IS NULL AND born IS NULL AND note = '  '");
        Assert.True(ok.State == ExecState.Done, string.Join("\n", h.Messages(eid)));
        Assert.Equal(1L, ((Int64Array)b[0].Column(0)).GetValue(0));
    }
}
