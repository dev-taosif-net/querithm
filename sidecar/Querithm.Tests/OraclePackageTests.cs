using System.Data.Common;
using Querithm.Contract;
using Querithm.Providers.Oracle;
using static Querithm.Providers.Oracle.OracleMetadata;

namespace Querithm.Tests;

/// <summary>
/// D4 S08 unit tests for Oracle packages as whole objects: a specification and its body assembled from canned dictionary
/// rows (same name, the body naming its specification, a missing body explained), markers that follow the other part,
/// members as display text (overloads apart), definition routing, the ALL_SOURCE fallback's qualified name and the
/// whole-object scripts. The server-side behaviour is <c>Integration/OraclePackageIntegrationTests</c>.
/// </summary>
public class OraclePackageTests
{
    static StampRow Row(long id, string type, string owner = "APP", string name = "PKG", string? status = "VALID", string? flag = null) =>
        new(id, owner, name, type, id * 10, null, null, status, flag);

    [Fact]
    public void A_specification_and_a_body_with_the_same_name_are_two_objects_and_the_body_names_its_specification()
    {
        var stamps = Assemble([Row(1, "PACKAGE", flag: "U"), Row(2, "PACKAGE BODY"), Row(3, "PROCEDURE", name: "PKG")]).ToDictionary(s => s.Id);
        Assert.Equal((ObjectKind.Package, "PKG"), (stamps[1].Kind, stamps[1].Name));
        Assert.Equal((ObjectKind.PackageBody, "PKG", (long?)1), (stamps[2].Kind, stamps[2].Name, stamps[2].SpecObjectId));
        Assert.Null(stamps[1].Note); // it has its body
        Assert.Null(stamps[1].SpecObjectId);
        Assert.Null(stamps[2].ParentObjectId); // ParentObjectId stays the trigger's table
        Assert.Null(stamps[3].SpecObjectId); // a procedure of the same name is not part of the package
    }

    [Fact]
    public void Names_are_matched_exactly_and_per_owner()
    {
        var stamps = Assemble([Row(1, "PACKAGE", name: "Pkg", flag: "U"), Row(2, "PACKAGE BODY", name: "PKG"), Row(3, "PACKAGE", owner: "OTHER"), Row(4, "PACKAGE BODY", owner: "OTHER")])
            .ToDictionary(s => s.Id);
        Assert.Null(stamps[2].SpecObjectId); // "Pkg" is not PKG
        Assert.Equal(OracleMetadata.NoBody, stamps[1].Note);
        Assert.Equal(3, stamps[4].SpecObjectId);
    }

    [Fact]
    public void A_specification_without_a_body_says_whether_it_has_none_or_the_login_cannot_see_it()
    {
        var stamps = Assemble([Row(1, "PACKAGE", flag: "U"), Row(2, "PACKAGE", owner: "OWN"), Row(3, "PACKAGE", owner: "OWN", name: "BAD", status: "INVALID")]).ToDictionary(s => s.Id);
        Assert.Equal("no body", stamps[1].Note); // the login's own user always sees its bodies
        Assert.Equal("body not visible", stamps[2].Note); // another schema: EXECUTE shows the specification only
        Assert.Equal("invalid, body not visible", stamps[3].Note);
    }

    [Fact]
    public void A_body_appearing_or_going_changes_the_specifications_marker_and_the_specification_moves_its_bodys()
    {
        long Spec(params StampRow[] rows) => Assemble(rows).Single(s => s.Kind == ObjectKind.Package).Marker;
        var alone = Spec(Row(1, "PACKAGE", flag: "U"));
        var withBody = Spec(Row(1, "PACKAGE", flag: "U"), Row(2, "PACKAGE BODY"));
        var otherBody = Spec(Row(1, "PACKAGE", flag: "U"), Row(5, "PACKAGE BODY"));
        Assert.NotEqual(alone, withBody);
        Assert.NotEqual(withBody, otherBody); // dropped and created again: a new id
        Assert.Equal(withBody, Spec(Row(1, "PACKAGE", flag: "U"), Row(2, "PACKAGE BODY"))); // stable across refreshes
        Assert.All(new[] { alone, withBody, otherBody }, m => Assert.InRange(m, 0, 0x0FFF_FFFF_FFFF_FFFFL));
        var body1 = Assemble([Row(1, "PACKAGE"), Row(2, "PACKAGE BODY")]).Single(s => s.Id == 2).Marker;
        var body9 = Assemble([Row(9, "PACKAGE"), Row(2, "PACKAGE BODY")]).Single(s => s.Id == 2).Marker;
        Assert.NotEqual(body1, body9);
    }

    [Fact]
    public void Members_are_display_text_one_line_per_overload_with_modes_and_return_types()
    {
        var members = AssembleMembers(
        [
            new MemberArg(1, 1, "P", 1, "A", "NUMBER", null, null, "IN"),
            new MemberArg(1, 2, "P", 1, "A", "VARCHAR2", null, null, "IN"),
            new MemberArg(1, 2, "P", 2, "B", "NUMBER", null, null, "IN/OUT"),
            new MemberArg(1, 3, "F", 0, null, "NUMBER", null, null, "OUT"),
            new MemberArg(1, 3, "F", 1, "X", "DATE", null, null, "IN"),
            new MemberArg(1, 4, "Q", 1, null, null, null, null, "IN"), // the dictionary's placeholder for no arguments
            new MemberArg(1, 5, "R", null, null, null, null, null, null), // no argument row at all
            new MemberArg(1, 6, "T", 1, "O", "OBJECT", "APP", "PT", "OUT"),
            new MemberArg(2, 1, "OTHER", null, null, null, null, null, null),
        ]);
        Assert.Equal(["P(A IN NUMBER)", "P(A IN VARCHAR2, B IN/OUT NUMBER)", "F(X IN DATE) RETURN NUMBER", "Q", "R", "T(O OUT APP.PT)"],
            members.Where(m => m.ObjectId == 1).Select(m => m.Text));
        Assert.Equal([1, 2, 3, 4, 5, 6], members.Where(m => m.ObjectId == 1).Select(m => m.Ordinal));
        Assert.Equal("OTHER", members.Single(m => m.ObjectId == 2).Text);
    }

    [Fact]
    public void Each_part_is_read_as_its_own_DBMS_METADATA_and_ALL_SOURCE_type()
    {
        Assert.Equal(("PACKAGE_SPEC", "PACKAGE_BODY"), (MetadataType(ObjectKind.Package), MetadataType(ObjectKind.PackageBody)));
        Assert.Equal(("PACKAGE", "PACKAGE"), SourceTypes(ObjectKind.Package)); // the specification alone, never its body
        Assert.Equal(("PACKAGE BODY", "PACKAGE BODY"), SourceTypes(ObjectKind.PackageBody));
        Assert.Equal(("TYPE", "TYPE BODY"), SourceTypes(ObjectKind.Type));
        Assert.Null(SourceTypes(ObjectKind.Table));
    }

    [Theory]
    [InlineData("PACKAGE   pkg AS PROCEDURE p; END;", "PACKAGE OWN.PKG AS PROCEDURE p; END;")]
    [InlineData("PACKAGE BODY pkg AS PROCEDURE p IS BEGIN NULL; END; END;", "PACKAGE BODY OWN.PKG AS PROCEDURE p IS BEGIN NULL; END; END;")]
    [InlineData("EDITIONABLE PACKAGE \"own\".\"pkg\" AS END;", "EDITIONABLE PACKAGE OWN.PKG AS END;")]
    public void The_source_fallback_names_a_package_with_its_owner(string unit, string expected) =>
        Assert.Equal(expected, Qualify(unit, "OWN.PKG"));

    [Theory]
    [InlineData("CREATE PACKAGE pkg AS END;\n/", "CREATE OR REPLACE PACKAGE pkg AS END;\n/")]
    [InlineData("CREATE EDITIONABLE PACKAGE BODY pkg AS END;\n/", "CREATE OR REPLACE EDITIONABLE PACKAGE BODY pkg AS END;\n/")]
    [InlineData("CREATE OR REPLACE PACKAGE pkg AS END;", "CREATE OR REPLACE PACKAGE pkg AS END;")]
    public void A_package_definition_opens_for_editing_as_CREATE_OR_REPLACE(string definition, string expected) =>
        Assert.Equal(expected, Editable(definition));

    // ------------------------------------------------------------------------------------------ whole-object scripts

    const string SpecDdl = "CREATE OR REPLACE EDITIONABLE PACKAGE \"APP\".\"PKG\" AS\n  PROCEDURE p(a NUMBER);\n  PROCEDURE p(a VARCHAR2);\nEND;\n/";
    const string BodyDdl = "CREATE PACKAGE BODY \"APP\".\"PKG\" AS\n  PROCEDURE p(a NUMBER) IS BEGIN NULL; END;\n  PROCEDURE p(a VARCHAR2) IS BEGIN NULL; END;\nEND;\n/";

    static (OracleScripter Scripter, List<ObjectRef> Asked) Scripter(bool bodyVisible = true)
    {
        var asked = new List<ObjectRef>();
        var s = new OracleScripter((_, o, _) =>
        {
            asked.Add(o);
            return Task.FromResult<string?>(o.Kind == ObjectKind.Package ? SpecDdl : bodyVisible ? BodyDdl : null);
        }, Editable);
        return (s, asked);
    }

    static ObjectRef Ref(ObjectKind kind, string name = "PKG") => new() { Schema = "APP", Name = name, Kind = kind, ObjectId = 7 };

    static string Script(OracleScripter s, ObjectKind kind, ScriptKind script, bool withBody = false, string name = "PKG") =>
        s.ScriptAsync(null!, Ref(kind, name), script, withBody, default).GetAwaiter().GetResult();

    [Fact]
    public void CREATE_of_a_specification_is_the_specification_alone_and_with_body_both_in_order()
    {
        var (s, asked) = Scripter();
        Assert.Equal(SpecDdl, Script(s, ObjectKind.Package, ScriptKind.Create));
        Assert.Equal([ObjectKind.Package], asked.Select(a => a.Kind));
        var both = Script(s, ObjectKind.Package, ScriptKind.Create, withBody: true);
        Assert.Equal(SpecDdl + "\n\n" + Editable(BodyDdl), both);
        Assert.Contains("CREATE OR REPLACE PACKAGE BODY", both);
        Assert.True(both.IndexOf("PACKAGE BODY", StringComparison.Ordinal) > both.IndexOf("EDITIONABLE PACKAGE", StringComparison.Ordinal));
        // Overloaded members stay in the definition as written: the package is one object, not its members.
        Assert.Equal(2, both.Split("PROCEDURE p(a VARCHAR2)").Length - 1);
    }

    [Fact]
    public void CREATE_of_a_body_is_the_body_alone()
    {
        var (s, asked) = Scripter();
        Assert.Equal(Editable(BodyDdl), Script(s, ObjectKind.PackageBody, ScriptKind.Create, withBody: true));
        Assert.Equal([ObjectKind.PackageBody], asked.Select(a => a.Kind));
    }

    [Fact]
    public void ALTER_recompiles_the_part_and_DROP_says_what_it_drops()
    {
        var (s, asked) = Scripter();
        Assert.Equal("ALTER PACKAGE APP.PKG COMPILE SPECIFICATION;", Script(s, ObjectKind.Package, ScriptKind.Alter));
        Assert.Equal("ALTER PACKAGE APP.PKG COMPILE BODY;", Script(s, ObjectKind.PackageBody, ScriptKind.Alter));
        Assert.Equal($"{OracleScripter.DropsBoth}\nDROP PACKAGE APP.PKG;", Script(s, ObjectKind.Package, ScriptKind.Drop));
        Assert.Equal("DROP PACKAGE BODY APP.PKG;", Script(s, ObjectKind.PackageBody, ScriptKind.Drop));
        Assert.Equal("DROP PACKAGE BODY APP.\"Mixed Case\";", Script(s, ObjectKind.PackageBody, ScriptKind.Drop, name: "Mixed Case"));
        Assert.Empty(asked); // no definition needed
    }

    [Fact]
    public void DROP_and_CREATE_of_a_specification_re_creates_its_body_too()
    {
        var (s, _) = Scripter();
        Assert.Equal($"DROP PACKAGE APP.PKG;\n\n{SpecDdl}\n\n{Editable(BodyDdl)}", Script(s, ObjectKind.Package, ScriptKind.DropCreate));
        Assert.Equal($"DROP PACKAGE BODY APP.PKG;\n\n{Editable(BodyDdl)}", Script(s, ObjectKind.PackageBody, ScriptKind.DropCreate));
    }

    [Fact]
    public void A_body_the_login_cannot_read_is_left_out_with_the_reason_and_a_drop_warns_it_would_be_lost()
    {
        var (s, _) = Scripter(bodyVisible: false);
        Assert.Equal($"{OracleScripter.BodyLeftOut}\n\n{SpecDdl}", Script(s, ObjectKind.Package, ScriptKind.Create, withBody: true));
        var dropCreate = Script(s, ObjectKind.Package, ScriptKind.DropCreate);
        Assert.StartsWith(OracleScripter.BodyLost, dropCreate);
        Assert.Contains("DROP PACKAGE APP.PKG;", dropCreate);
    }

    [Theory]
    [InlineData(ScriptKind.Execute)]
    [InlineData(ScriptKind.SelectTop)]
    [InlineData(ScriptKind.Enable)]
    [InlineData(ScriptKind.Disable)]
    public void A_package_is_not_run_as_a_whole(ScriptKind kind)
    {
        var (s, _) = Scripter();
        var ex = Assert.Throws<NotSupportedException>(() => Script(s, ObjectKind.Package, kind));
        Assert.Equal(OracleScripter.NotRun, ex.Message);
    }

    [Fact]
    public void A_package_whose_definition_is_gone_says_so()
    {
        var s = new OracleScripter((_, _, _) => Task.FromResult<string?>(null), Editable);
        var ex = Assert.Throws<InvalidOperationException>(() => Script(s, ObjectKind.Package, ScriptKind.Create));
        Assert.Contains("APP.PKG", ex.Message);
    }

}
