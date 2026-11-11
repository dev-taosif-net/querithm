using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Intelligence.Oracle;
using Querithm.Providers.Oracle;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S08 against the Oracle Free fixture (QUERITHM_IT=1): packages as whole objects. A specification and a body with the
/// same name (overloaded members), a specification without a body, another schema's package the least-privileged user may
/// only execute (body not visible) and the catalog-role user may also debug (body visible), a body appearing later, the
/// definitions of each part, whole-object scripts that run unchanged, and a refresh through the sidecar that stores and
/// returns the packages.
/// </summary>
public sealed class OraclePackageIntegrationTests
{
    const string App = OracleIntegrationTests.AppUser, Mon = OracleIntegrationTests.MonitorUser;
    const string AppSchema = "QUERITHM_APP", Own = "QUERITHM_OWN";

    static Profile ProfileOf(string user) => new()
    {
        Id = "", Name = "ora-pkg", Engine = Engine.Oracle, Host = "127.0.0.1", Port = OracleIntegrationTests.Port, Auth = AuthKind.Password, Username = user,
        DatabaseDefault = OracleIntegrationTests.Service, TlsMode = TlsMode.Disable, TimeoutS = 30,
    };

    static string Secret(string user) => FixtureSecrets.Password(user == App ? "oracle" : "oracle-mon");

    static async Task<DbConnection> OpenAsync(string user = App)
    {
        var dir = Directory.CreateTempSubdirectory("qora-pkg-").FullName;
        return await new OracleProvider(dir).OpenAsync(ProfileOf(user), Secret(user), null, null, Timeout());
    }

    static CancellationToken Timeout(int seconds = 120) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    static async Task ExecAsync(DbConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(Timeout());
    }

    /// <summary>Each batch of a script as the splitter cuts it, run as it is (a CREATE … / unit keeps its END;).</summary>
    static async Task RunScriptAsync(DbConnection c, string script)
    {
        foreach (var st in OracleSplitter.Split(script).Where(s => s.Kind != OracleStatementKind.ClientCommand))
        {
            var text = script[st.Start..st.End];
            if (st.Code.Any()) await ExecAsync(c, text);
        }
    }

    static Task DropPackageAsync(DbConnection c, string name) => ExecAsync(c, $"""
        BEGIN EXECUTE IMMEDIATE 'DROP PACKAGE {name}'; EXCEPTION WHEN OTHERS THEN IF SQLCODE <> -4043 THEN RAISE; END IF; END;
        """);

    static async Task<string?> StatusAsync(DbConnection c, string name, string type)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT status FROM user_objects WHERE object_name = '{name}' AND object_type = '{type}'";
        return (string?)await cmd.ExecuteScalarAsync(Timeout());
    }

    static ObjectStamp One(IEnumerable<ObjectStamp> stamps, string schema, string name, ObjectKind kind) =>
        Assert.Single(stamps, s => s.Schema == schema && s.Name == name && s.Kind == kind);

    static readonly SemaphoreSlim Once = new(1, 1);
    static bool _made;

    static async Task SchemaAsync()
    {
        await Once.WaitAsync();
        try
        {
            if (_made) return;
            await using var c = await OpenAsync();
            await DropPackageAsync(c, "Q_S08_PKG");
            await DropPackageAsync(c, "Q_S08_NOBODY");
            await DropPackageAsync(c, "Q_S08_LATER");
            await RunScriptAsync(c, """
                CREATE OR REPLACE PACKAGE q_s08_pkg AS
                  PROCEDURE p(a NUMBER);
                  PROCEDURE p(a VARCHAR2, b OUT NUMBER);
                  FUNCTION f(x DATE) RETURN NUMBER;
                  PROCEDURE q;
                END q_s08_pkg;
                /
                CREATE OR REPLACE PACKAGE BODY q_s08_pkg AS
                  PROCEDURE p(a NUMBER) IS BEGIN NULL; END;
                  PROCEDURE p(a VARCHAR2, b OUT NUMBER) IS BEGIN b := 1; END;
                  FUNCTION f(x DATE) RETURN NUMBER IS BEGIN RETURN 1; END;
                  PROCEDURE q IS BEGIN NULL; END;
                END q_s08_pkg;
                /
                CREATE OR REPLACE PACKAGE q_s08_nobody AS
                  c_limit CONSTANT NUMBER := 10;
                END q_s08_nobody;
                /
                """);
            _made = true;
        }
        finally { Once.Release(); }
    }

    [IntegrationFact]
    public async Task A_specification_and_a_body_with_the_same_name_are_listed_apart_with_the_overloaded_members()
    {
        await SchemaAsync();
        await using var c = await OpenAsync();
        var meta = new OracleMetadata();
        var stamps = await meta.GetStampsAsync(c, Timeout());
        Assert.Empty(meta.Notes(c));
        var spec = One(stamps, AppSchema, "Q_S08_PKG", ObjectKind.Package);
        var body = One(stamps, AppSchema, "Q_S08_PKG", ObjectKind.PackageBody);
        Assert.NotEqual(spec.Id, body.Id);
        Assert.Equal(spec.Id, body.SpecObjectId);
        Assert.Null(spec.Note);
        Assert.Null(body.ParentObjectId);
        Assert.Equal(OracleMetadata.NoBody, One(stamps, AppSchema, "Q_S08_NOBODY", ObjectKind.Package).Note);
        Assert.DoesNotContain(stamps, s => s.Name == "Q_S08_NOBODY" && s.Kind == ObjectKind.PackageBody);

        var details = await meta.GetDetailsAsync(c, [spec.Id, body.Id], Timeout());
        Assert.Equal(["P(A IN NUMBER)", "P(A IN VARCHAR2, B OUT NUMBER)", "F(X IN DATE) RETURN NUMBER", "Q"],
            details.Members!.Where(m => m.ObjectId == spec.Id).OrderBy(m => m.Ordinal).Select(m => m.Text));
        Assert.DoesNotContain(details.Members!, m => m.ObjectId == body.Id);
        Assert.Empty(details.Params); // members are not a routine's parameters
    }

    [IntegrationFact]
    public async Task Each_part_has_its_own_definition_with_every_overload_as_written()
    {
        await SchemaAsync();
        await using var c = await OpenAsync();
        var meta = new OracleMetadata();
        var spec = await meta.GetDefinitionAsync(c, new ObjectRef { Schema = AppSchema, Name = "Q_S08_PKG", Kind = ObjectKind.Package }, Timeout());
        Assert.StartsWith("CREATE OR REPLACE EDITIONABLE PACKAGE \"QUERITHM_APP\".\"Q_S08_PKG\"", spec);
        Assert.DoesNotContain("PACKAGE BODY", spec);
        Assert.Contains("PROCEDURE p(a NUMBER);", spec);
        Assert.Contains("PROCEDURE p(a VARCHAR2, b OUT NUMBER);", spec);
        Assert.EndsWith("/", spec!.TrimEnd());
        var body = await meta.GetDefinitionAsync(c, new ObjectRef { Schema = AppSchema, Name = "Q_S08_PKG", Kind = ObjectKind.PackageBody }, Timeout());
        Assert.StartsWith("CREATE OR REPLACE EDITIONABLE PACKAGE BODY \"QUERITHM_APP\".\"Q_S08_PKG\"", body);
        Assert.Contains("PROCEDURE p(a VARCHAR2, b OUT NUMBER) IS BEGIN b := 1; END;", body);
        // No body: GET_DDL answers ORA-31603 and ALL_SOURCE has nothing either.
        Assert.Null(await meta.GetDefinitionAsync(c, new ObjectRef { Schema = AppSchema, Name = "Q_S08_NOBODY", Kind = ObjectKind.PackageBody }, Timeout()));
    }

    [IntegrationFact]
    public async Task Another_schemas_package_shows_its_body_only_to_a_login_that_may_debug_it()
    {
        // querithm_app holds EXECUTE on QUERITHM_OWN.SHARED_PKG; querithm_mon holds EXECUTE and DEBUG (01-users.sh).
        await using var c = await OpenAsync();
        var meta = new OracleMetadata();
        var stamps = await meta.GetStampsAsync(c, Timeout());
        var spec = One(stamps, Own, "SHARED_PKG", ObjectKind.Package);
        Assert.Equal(OracleMetadata.BodyNotVisible, spec.Note);
        Assert.DoesNotContain(stamps, s => s.Schema == Own && s.Kind == ObjectKind.PackageBody);
        var members = (await meta.GetDetailsAsync(c, [spec.Id], Timeout())).Members!;
        Assert.Equal(["P(A IN NUMBER)", "P(A IN VARCHAR2)"], members.Select(m => m.Text)); // the specification is readable
        // GET_DDL refuses another schema's package (ORA-31603): the specification comes from ALL_SOURCE, qualified.
        var def = await meta.GetDefinitionAsync(c, new ObjectRef { Schema = Own, Name = "SHARED_PKG", Kind = ObjectKind.Package }, Timeout());
        Assert.StartsWith("CREATE OR REPLACE PACKAGE QUERITHM_OWN.SHARED_PKG", def);
        Assert.Null(await meta.GetDefinitionAsync(c, new ObjectRef { Schema = Own, Name = "SHARED_PKG", Kind = ObjectKind.PackageBody }, Timeout()));
        var withBody = await new OracleScripter(meta).ScriptAsync(c, new ObjectRef { Schema = Own, Name = "SHARED_PKG", Kind = ObjectKind.Package }, ScriptKind.Create, withBody: true, Timeout());
        Assert.StartsWith(OracleScripter.BodyLeftOut, withBody);
        var dropCreate = await new OracleScripter(meta).ScriptAsync(c, new ObjectRef { Schema = Own, Name = "SHARED_PKG", Kind = ObjectKind.Package }, ScriptKind.DropCreate, Timeout());
        Assert.StartsWith(OracleScripter.BodyLost, dropCreate);

        await using var m = await OpenAsync(Mon);
        var monStamps = await meta.GetStampsAsync(m, Timeout());
        var monSpec = One(monStamps, Own, "SHARED_PKG", ObjectKind.Package);
        Assert.Null(monSpec.Note);
        Assert.Equal(monSpec.Id, One(monStamps, Own, "SHARED_PKG", ObjectKind.PackageBody).SpecObjectId);
        Assert.Equal(spec.Id, monSpec.Id); // one object, whoever looks
        var monBody = await meta.GetDefinitionAsync(m, new ObjectRef { Schema = Own, Name = "SHARED_PKG", Kind = ObjectKind.PackageBody }, Timeout());
        Assert.Contains("PACKAGE BODY", monBody);
    }

    [IntegrationFact]
    public async Task A_body_created_later_moves_the_specifications_marker_and_clears_its_note()
    {
        await SchemaAsync();
        await using var c = await OpenAsync();
        var meta = new OracleMetadata();
        await DropPackageAsync(c, "Q_S08_LATER");
        await RunScriptAsync(c, "CREATE OR REPLACE PACKAGE q_s08_later AS PROCEDURE p; END;\n/");
        var before = One(await meta.GetStampsAsync(c, Timeout()), AppSchema, "Q_S08_LATER", ObjectKind.Package);
        Assert.Equal(OracleMetadata.NoBody, before.Note);
        await RunScriptAsync(c, "CREATE OR REPLACE PACKAGE BODY q_s08_later AS PROCEDURE p IS BEGIN NULL; END; END;\n/");
        var stamps = await meta.GetStampsAsync(c, Timeout());
        var after = One(stamps, AppSchema, "Q_S08_LATER", ObjectKind.Package);
        Assert.Equal(before.Id, after.Id);
        Assert.NotEqual(before.Marker, after.Marker);
        Assert.Null(after.Note);
        var body = One(stamps, AppSchema, "Q_S08_LATER", ObjectKind.PackageBody);
        // The body's source changes its own marker, not the specification's.
        await RunScriptAsync(c, "CREATE OR REPLACE PACKAGE BODY q_s08_later AS PROCEDURE p IS BEGIN NULL; NULL; END; END;\n/");
        var again = await meta.GetStampsAsync(c, Timeout());
        Assert.Equal(after.Marker, One(again, AppSchema, "Q_S08_LATER", ObjectKind.Package).Marker);
        Assert.NotEqual(body.Marker, One(again, AppSchema, "Q_S08_LATER", ObjectKind.PackageBody).Marker);
        await DropPackageAsync(c, "Q_S08_LATER");
    }

    [IntegrationFact]
    public async Task Whole_object_scripts_run_unchanged_and_leave_the_package_valid()
    {
        await SchemaAsync();
        await using var c = await OpenAsync();
        var scripter = new OracleScripter(new OracleMetadata());
        var spec = new ObjectRef { Schema = AppSchema, Name = "Q_S08_PKG", Kind = ObjectKind.Package };
        var body = spec with { Kind = ObjectKind.PackageBody };

        var both = await scripter.ScriptAsync(c, spec, ScriptKind.Create, withBody: true, Timeout());
        Assert.Equal(2, OracleSplitter.Split(both).Count(s => s.Kind == OracleStatementKind.PlSql));
        await RunScriptAsync(c, both);
        var dropCreate = await scripter.ScriptAsync(c, spec, ScriptKind.DropCreate, Timeout());
        await RunScriptAsync(c, dropCreate); // DROP PACKAGE, then the specification and the body again
        Assert.Equal(("VALID", "VALID"), (await StatusAsync(c, "Q_S08_PKG", "PACKAGE"), await StatusAsync(c, "Q_S08_PKG", "PACKAGE BODY")));
        await RunScriptAsync(c, await scripter.ScriptAsync(c, body, ScriptKind.DropCreate, Timeout()));
        await RunScriptAsync(c, await scripter.ScriptAsync(c, spec, ScriptKind.Alter, Timeout()));
        await RunScriptAsync(c, await scripter.ScriptAsync(c, body, ScriptKind.Alter, Timeout()));
        Assert.Equal(("VALID", "VALID"), (await StatusAsync(c, "Q_S08_PKG", "PACKAGE"), await StatusAsync(c, "Q_S08_PKG", "PACKAGE BODY")));

        await DropPackageAsync(c, "Q_S08_TMP");
        await RunScriptAsync(c, "CREATE PACKAGE q_s08_tmp AS PROCEDURE p; END;\n/\nCREATE PACKAGE BODY q_s08_tmp AS PROCEDURE p IS BEGIN NULL; END; END;\n/");
        var tmp = new ObjectRef { Schema = AppSchema, Name = "Q_S08_TMP", Kind = ObjectKind.PackageBody };
        await RunScriptAsync(c, await scripter.ScriptAsync(c, tmp, ScriptKind.Drop, Timeout()));
        Assert.Null(await StatusAsync(c, "Q_S08_TMP", "PACKAGE BODY"));
        Assert.Equal("VALID", await StatusAsync(c, "Q_S08_TMP", "PACKAGE")); // the body alone was dropped
        await RunScriptAsync(c, await scripter.ScriptAsync(c, tmp with { Kind = ObjectKind.Package }, ScriptKind.Drop, Timeout()));
        Assert.Null(await StatusAsync(c, "Q_S08_TMP", "PACKAGE"));
    }

    [IntegrationFact]
    public async Task A_refresh_through_the_sidecar_stores_packages_and_scripts_them_by_RPC()
    {
        await SchemaAsync();
        var dir = Directory.CreateTempSubdirectory("qora-pkg-host-").FullName;
        await using var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance,
            new ProviderRegistry(EngineSetup.Providers(dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle")), dir);
        var rpc = new RpcServer(services, () => { });
        var p = await rpc.ProfileSave(ProfileOf(App));
        await rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = Secret(App) });
        var db = OracleIntegrationTests.Service;
        await rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = db, Full = true });
        await services.Metadata.WaitAsync(p.Id, db);
        var snap = await rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = db });
        Assert.Null(snap.Error);
        var spec = Assert.Single(snap.Objects, o => o.Name == "Q_S08_PKG" && o.Kind == ObjectKind.Package);
        var body = Assert.Single(snap.Objects, o => o.Name == "Q_S08_PKG" && o.Kind == ObjectKind.PackageBody);
        Assert.Equal(spec.Id, body.SpecObjectId);
        Assert.Equal(4, spec.Members!.Length);
        Assert.Null(body.Members);
        Assert.Equal("body not visible", Assert.Single(snap.Objects, o => o.Schema == Own && o.Name == "SHARED_PKG").Note);

        var sid = services.Sessions.All.Single().Id;
        var objRef = new ObjectRef { ProfileId = p.Id, Database = db, Schema = AppSchema, Name = "Q_S08_PKG", Kind = ObjectKind.Package, ObjectId = spec.Id };
        var script = await rpc.MetaScript(new ScriptParams { SessionId = sid, Object = objRef, Kind = ScriptKind.Create, WithBody = true });
        Assert.Contains("PACKAGE BODY", script.Sql);
        Assert.DoesNotContain("PACKAGE BODY", (await rpc.MetaScript(new ScriptParams { SessionId = sid, Object = objRef, Kind = ScriptKind.Create })).Sql);
        // Modify / Apply: the editable definition of each part, and its hash for the Apply conflict check.
        var def = await rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = objRef with { Kind = ObjectKind.PackageBody, ObjectId = body.Id } });
        Assert.StartsWith("CREATE OR REPLACE EDITIONABLE PACKAGE BODY", def.Sql);
        Assert.False(string.IsNullOrEmpty(def.DefinitionHash));
    }
}
