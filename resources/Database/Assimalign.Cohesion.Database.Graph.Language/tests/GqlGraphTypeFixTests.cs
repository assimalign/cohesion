using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlGraphTypeFixTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Schema scalar names: explicit spellings work in both declaration paths (#1806)")]
    [InlineData("BOOLEAN", GqlSchemaPropertyType.Boolean)]
    [InlineData("BOOL", GqlSchemaPropertyType.Boolean)]
    [InlineData("INT8", GqlSchemaPropertyType.Int8)]
    [InlineData("INT16", GqlSchemaPropertyType.Int16)]
    [InlineData("INT32", GqlSchemaPropertyType.Int32)]
    [InlineData("INT64", GqlSchemaPropertyType.Int64)]
    [InlineData("INTEGER", GqlSchemaPropertyType.Int64)]
    [InlineData("INT", GqlSchemaPropertyType.Int64)]
    [InlineData("FLOAT32", GqlSchemaPropertyType.Float32)]
    [InlineData("FLOAT64", GqlSchemaPropertyType.Float64)]
    [InlineData("FLOAT", GqlSchemaPropertyType.Float64)]
    [InlineData("DECIMAL", GqlSchemaPropertyType.Decimal)]
    [InlineData("STRING", GqlSchemaPropertyType.String)]
    public void Parse_ScalarSpelling_ShouldMapBothPaths(string spelling, GqlSchemaPropertyType type)
    {
        // Arrange / Act
        var graph = (GqlQueryStatement)new GqlQueryParser().Parse("ALTER CURRENT GRAPH TYPE SET {(:A => {p :: " + spelling.ToLowerInvariant() + "})}");
        var constraint = (GqlQueryStatement)new GqlQueryParser().Parse("CREATE CONSTRAINT c FOR (n:A) REQUIRE n.p IS :: " + spelling);
        // Assert
        graph.Diagnostics.ShouldBeEmpty();
        constraint.Diagnostics.ShouldBeEmpty();
        graph.GqlExpression.SchemaCommand!.GraphType!.Nodes.Single().Properties.Single().Type.ShouldBe(type);
        constraint.GqlExpression.SchemaCommand!.Type.ShouldBe(type);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Graph type blank names: a located invalid-name diagnostic (#1806)")]
    public void Parse_BlankGraphTypeName_ShouldDescribeInvalidName()
    {
        // Arrange / Act
        const string text = "ALTER CURRENT GRAPH TYPE SET {(:\" \" =>)}";
        var result = new GqlQueryParser().Parse(text);
        // Assert
        var diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CG03011");
        diagnostic.Start.ShouldBe(text.IndexOf('"'));
        diagnostic.Message.ShouldContain("non-whitespace", Case.Sensitive);
    }
    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Graph type AST: kind identifies the command (#1806)")]
    public void Parse_GraphType_ShouldIdentifyKind()
    {
        // Arrange / Act
        var result = (GqlQueryStatement)new GqlQueryParser().Parse("ALTER CURRENT GRAPH TYPE SET {}");
        // Assert
        result.GqlExpression.SchemaCommand!.Kind.ToString().ShouldBe("GraphType");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Graph type diagnostics: point at the offending name (#1806)")]
    [InlineData("SET {(:N =>),(:N =>)}", "N", true)]
    [InlineData("SET {(:N => {p :: STRING,p :: INT})}", "p", true)]
    [InlineData("SET {(:N => :I & I)}", "I", true)]
    [InlineData("SET {(missing)-[:R =>]->(:B)}", "missing", false)]
    [InlineData("SET {(:B)-[:R =>]->()}", "()", true)]
    public void Parse_InvalidName_ShouldLocateName(string body, string name, bool last)
    {
        // Arrange / Act
        string text = "ALTER CURRENT GRAPH TYPE " + body;
        var result = new GqlQueryParser().Parse(text);
        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Start.ShouldBe(last ? text.LastIndexOf(name, StringComparison.Ordinal) : text.IndexOf(name, StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Graph type recognized unsupported forms: one capability diagnostic (#1806)")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {(p:Person => {name :: STRING}) REQUIRE p.name IS UNIQUE}")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {(:A =>), (:B =>), (:A)-[r:R => {p :: STRING}]->(:B) REQUIRE r.p IS UNIQUE}")]
    [InlineData("ALTER CURRENT GRAPH TYPE DROP {(:Pet => :Resident)}")]
    [InlineData("ALTER CURRENT GRAPH TYPE DROP {(:Pet => {})}")]
    [InlineData("ALTER CURRENT GRAPH TYPE DROP {(:A)-[:R => {p :: STRING}]->(:B)}")]
    [InlineData("SHOW CURRENT USER")]
    [InlineData("SHOW CURRENT GRAPH TYPE AS GRAPH")]
    [InlineData("SHOW CURRENT GRAPH TYPE YIELD specification")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {(:A => {p :: STRING!})}")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {(:A => {p :: STRING | INTEGER})}")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {(:A => {p :: \"3\"})}")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {(:A => {p :: \"Int8, Int16\"})}")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {(:A => {p :: \"string\"})}")]
    [InlineData("CREATE CONSTRAINT c FOR (n:A) REQUIRE n.p IS :: \"3\"")]
    [InlineData("CREATE CONSTRAINT c FOR (n:A) REQUIRE n.p IS :: \"Int8, Int16\"")]
    public void Parse_UnsupportedForm_ShouldRefuseOnce(string text)
    {
        // Arrange / Act
        var result = new GqlQueryParser().Parse(text);
        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Embedded graph type command: later unsupported constructs are retained (#1806)")]
    public void Parse_EmbeddedAlter_ShouldReportLaterConstructs()
    {
        // Arrange / Act
        var result = new GqlQueryParser().Parse("RETURN 1 ALTER CURRENT GRAPH TYPE SET {} USE g");
        // Assert
        result.Diagnostics.Count().ShouldBe(2);
        result.Diagnostics.All(diagnostic => diagnostic.Code == "CK00001").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Graph type variables: an edge cannot duplicate a node alias (#1806)")]
    public void Parse_DuplicateEdgeVariable_ShouldRefuse()
    {
        // Arrange / Act
        var result = new GqlQueryParser().Parse("ALTER CURRENT GRAPH TYPE SET {(a:A =>), (:A)-[a:R =>]->(:A)}");
        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03011");
    }
}
