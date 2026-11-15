using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlGraphTypeParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Graph type DDL: canonical forms reparse every type shape")]
    [InlineData("SET {}")]
    [InlineData("ADD {}")]
    [InlineData("ALTER {}")]
    [InlineData("DROP {}")]
    [InlineData("SET {(:A =>), (:B => :Z & Y {p :: INT NOT NULL, q BOOL, r TYPED FLOAT})}")]
    [InlineData("ADD {(a:A IMPLIES :I), (b:B =>), (a)-[e:R IMPLIES {p :: STRING}]->(b)}")]
    [InlineData("ALTER {(:A)-[:R => {p :: DECIMAL NOT NULL}]->(:B)}")]
    [InlineData("DROP {(a:A =>), ()-[r:R =>]->()}")]
    [InlineData("SET {(:\"a\"\"b\" => :\"Odd name\" {\"p\"\"q\" :: STRING NOT NULL}), (:\"a\"\"b\")-[:\"Edge name\" =>]->(:\"a\"\"b\")}")]
    public void Parse_GraphType_ShouldRoundTripCanonicalForm(string body)
    {
        // Arrange
        var parser = new GqlQueryParser();
        // Act
        var first = (GqlQueryStatement)parser.Parse("ALTER CURRENT GRAPH TYPE " + body);
        string canonical = first.GqlExpression.ToString();
        var second = (GqlQueryStatement)parser.Parse(canonical);
        // Assert
        first.Diagnostics.ShouldBeEmpty();
        second.Diagnostics.ShouldBeEmpty();
        second.GqlExpression.ToString().ShouldBe(canonical);
        second.GqlExpression.SchemaCommand!.GraphType!.Nodes.Select(node => node.Label)
            .ShouldBe(first.GqlExpression.SchemaCommand!.GraphType!.Nodes.Select(node => node.Label).Order(StringComparer.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Graph type DDL: unsupported modes and constraints report one CK00001")]
    [InlineData("ALTER CURRENT GRAPH TYPE CLOSED")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET OPEN {}")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {} CLOSED")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {(:A => {p :: STRING IS UNIQUE})}")]
    [InlineData("ALTER CURRENT GRAPH TYPE ADD {(:A => {p :: INT IS NODE KEY})}")]
    [InlineData("ALTER CURRENT GRAPH TYPE ALTER {CONSTRAINT c FOR (n:A) REQUIRE n.p IS NOT NULL}")]
    [InlineData("ALTER CURRENT GRAPH TYPE DROP {CONSTRAINT c}")]
    [InlineData("ALTER CURRENT GRAPH TYPE SET {()-[:R =>]->()}")]
    [InlineData("ALTER GRAPH g")]
    [InlineData("RETURN 1 ALTER CURRENT GRAPH TYPE SET {}")]
    public void Parse_UnsupportedGraphType_ShouldRefuseOnce(string text)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        if (text == "ALTER GRAPH g")
        { statement.Diagnostics.ShouldHaveSingleItem().Message.ShouldNotBeNull().ShouldContain("The ALTER clause", Case.Sensitive); }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Graph type DDL: malformed and nested bodies refuse with a code")]
    [InlineData("SET {(:N)}", "CG00002")]
    [InlineData("SET {(:N =>),(:N =>)}", "CG03011")]
    [InlineData("SET {(:N => {p :: STRING,p :: INT})}", "CG03011")]
    [InlineData("SET {(:N => :N)}", "CG03011")]
    [InlineData("SET {(a)-[:R =>]->(:B)}", "CG03011")]
    [InlineData("SET {(:N => {p :: STRING NOT})}", "CG00002")]
    [InlineData("SET {(:N =>),}", "CG00002")]
    public void Parse_InvalidGraphType_ShouldRefuseWithCode(string body, string code)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse("ALTER CURRENT GRAPH TYPE " + body);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Graph type DDL: malformed nested-looking bodies refuse")]
    public void Parse_NestedLookingBody_ShouldRefuseMalformedInput()
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse("ALTER CURRENT GRAPH TYPE SET {" + new string('(', 20000) + "}");
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00002");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Graph type SHOW: canonical rendering is independent of source text")]
    public void Parse_ShowCurrentGraphType_ShouldRenderCanonicalForm()
    {
        // Arrange / Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse("show current graph type;");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.CatalogSurface.ShouldBe(GqlCatalogSurface.CurrentGraphType);
        statement.GqlExpression.ToString().ShouldBe("SHOW CURRENT GRAPH TYPE");
    }
}
