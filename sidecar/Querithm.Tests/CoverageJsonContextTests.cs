using System.Text.Json;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Store;

namespace Querithm.Tests;

/// <summary>The source-generated JSON metadata (the trimmed sidecar cannot use reflection) must round-trip every field of the saved model file.</summary>
public class JsonContextRoundTripTests
{
    static SchemaModel Full(Engine engine) => new()
    {
        Engine = engine, ServerVersion = "16.0", MajorVersion = 16, Database = "db", CapturedAt = 1_700_000_000_000,
        Schemas = ["dbo", "sales"], Extensions = ["pgcrypto"], NotCompared = ["2 table types are not compared"],
        Tables =
        [
            new ModelTable
            {
                Schema = "dbo", Name = "Orders",
                Columns =
                [
                    new ModelColumn { Name = "Id", Type = "int", Identity = "1,1", Collation = "Latin1_General_CI_AS" },
                    new ModelColumn { Name = "Total", Type = "decimal(10,2)", Nullable = true, Default = "((0))", DefaultName = "DF_T", DefaultSystemNamed = true },
                    new ModelColumn { Name = "Calc", Type = "int", Computed = "([Id]*2)", Persisted = true, Uses = ["Id"] },
                ],
                PrimaryKey = new ModelKey { Name = "PK_Orders", SystemNamed = true, Columns = ["Id"], Descending = [true], Kind = "CLUSTERED" },
                Uniques = [new ModelKey { Name = "UQ_T", Columns = ["Total", "Id"], Descending = [false, true] }],
                Checks = [new ModelCheck { Name = "CK", SystemNamed = true, Expression = "([Total]>=(0))", Columns = ["Total"] }],
                Indexes = [new ModelIndex { Name = "IX", Unique = true, Columns = ["Total"], Descending = [true], Include = ["Id"], Filter = "([Total]>0)", Kind = "NONCLUSTERED", Definition = "CREATE INDEX ..." }],
                Fks = [new ModelFk { Name = "FK", SystemNamed = true, Columns = ["Id"], RefSchema = "sales", RefTable = "C", RefColumns = ["Id"], OnDelete = "CASCADE", OnUpdate = "SET NULL", Deferrable = true }],
            },
        ],
        Modules =
        [
            new ModelModule
            {
                Schema = "dbo", Name = "v", Kind = ModuleKind.MaterializedView, Signature = "(a integer)", Returns = "integer", Definition = "SELECT 1",
                SchemaBound = true, Table = "Orders", Columns = ["Id int"], References = [new ModelRef("dbo", "Orders")],
            },
        ],
        Sequences = [new ModelSequence { Schema = "dbo", Name = "s", Type = "bigint", Start = "1", Increment = "2", Min = "1", Max = "100", Cycle = true }],
        Types = [new ModelType { Schema = "dbo", Name = "t", Kind = "enum", Values = ["a", "b"], Definition = "AS ENUM" }],
    };

    [Theory]
    [InlineData(Engine.Mssql)]
    [InlineData(Engine.Pg)]
    public void Schema_model_survives_a_save_and_load_with_every_field(Engine engine)
    {
        var model = Full(engine);
        var json = JsonSerializer.Serialize(model, Json.Context.SchemaModel);
        var back = JsonSerializer.Deserialize(json, Json.Context.SchemaModel)!;
        // Records compare arrays by reference, so compare the wire form of both.
        Assert.Equal(json, JsonSerializer.Serialize(back, Json.Context.SchemaModel));
        Assert.Equal(engine, back.Engine);
        var t = back.Tables.Single();
        Assert.Equal("Latin1_General_CI_AS", t.Columns[0].Collation);
        Assert.True(t.Columns[2].Persisted);
        Assert.Equal(["Id"], t.Columns[2].Uses!);
        Assert.Equal("CLUSTERED", t.PrimaryKey!.Kind);
        Assert.Equal([false, true], t.Uniques.Single().Descending!);
        Assert.Equal("([Total]>=(0))", t.Checks.Single().Expression);
        Assert.Equal("([Total]>0)", t.Indexes.Single().Filter);
        Assert.Equal("CASCADE", t.Fks.Single().OnDelete);
        Assert.True(t.Fks.Single().Deferrable);
        var m = back.Modules.Single();
        Assert.Equal(ModuleKind.MaterializedView, m.Kind);
        Assert.Equal(new ModelRef("dbo", "Orders"), m.References.Single());
        Assert.True(back.Sequences.Single().Cycle);
        Assert.Equal(["a", "b"], back.Types.Single().Values!);
        Assert.Equal(["pgcrypto"], back.Extensions);
        // The generated metadata and the reflection options agree on the wire format.
        Assert.Equal(JsonSerializer.Serialize(model, Json.Options), json);
    }

    [Fact]
    public void Missing_optional_fields_take_their_defaults_when_loading_a_model_file()
    {
        const string json = """{"format":1,"engine":"pg","database":"d","schemas":[],"extensions":[],"sequences":[],"types":[],"notCompared":[],"tables":[{"schema":"s","name":"t","columns":[{"name":"c","type":"int"}]}],"modules":[]}""";
        var model = JsonSerializer.Deserialize(json, Json.Context.SchemaModel)!;
        Assert.Equal(SchemaModel.CurrentFormat, model.Format);
        Assert.Empty(model.Sequences);
        Assert.Empty(model.Types);
        var table = model.Tables.Single();
        Assert.Null(table.PrimaryKey);
        Assert.Single(table.Columns);
        Assert.False(table.Columns[0].Nullable);
        Assert.Null(table.Columns[0].Default);
        Assert.Equal("NO ACTION", new ModelFk { Name = "x", Columns = [], RefSchema = "", RefTable = "", RefColumns = [] }.OnDelete);
    }

    [Fact]
    public void Model_files_with_unknown_enum_values_are_rejected_rather_than_guessed()
    {
        const string json = """{"engine":"oracle","database":"d","schemas":[],"tables":[],"modules":[]}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, Json.Context.SchemaModel));
    }
}

public class ProfileOptionsPersistenceTests
{
    [Fact]
    public void Profile_with_options_ca_path_timeout_and_ssh_round_trips_through_the_store()
    {
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var saved = profiles.Save(new Profile
        {
            Id = "", Name = "p", Engine = Engine.Pg, Host = "h", Port = 5432, Auth = AuthKind.Password, Username = "u",
            Options = new Dictionary<string, string> { ["application_name"] = "q" }, CaPath = "/etc/ca.crt", CommandTimeoutS = 45,
            Ssh = new SshSettings { Host = "bastion", Port = 2222, Username = "jump", Auth = SshAuthKind.PrivateKey, KeyPath = "/home/u/.ssh/id" },
        });
        var back = profiles.List().Single(p => p.Id == saved.Id);
        Assert.Equal("q", back.Options!["application_name"]);
        Assert.Equal("/etc/ca.crt", back.CaPath);
        Assert.Equal(45, back.CommandTimeoutS);
        Assert.Equal("bastion", back.Ssh!.Host);
        Assert.Equal(2222, back.Ssh.Port);
        Assert.Equal(SshAuthKind.PrivateKey, back.Ssh.Auth);
        Assert.Equal("/home/u/.ssh/id", back.Ssh.KeyPath);
    }
}
