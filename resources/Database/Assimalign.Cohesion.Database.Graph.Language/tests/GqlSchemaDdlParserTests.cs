using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlSchemaDdlParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Schema DDL: every supported form renders and reparses")]
    [InlineData("CREATE INDEX ix FOR (n:N) ON (n.p)")]
    [InlineData("CREATE INDEX IF NOT EXISTS FOR (n:N) ON (n.p)")]
    [InlineData("CREATE CONSTRAINT c FOR (n:N) REQUIRE n.p IS NOT NULL")]
    [InlineData("CREATE CONSTRAINT IF NOT EXISTS FOR (n:N) REQUIRE (n.p) IS :: INTEGER")]
    [InlineData("CREATE CONSTRAINT c IF NOT EXISTS FOR ()-[r:T]-() REQUIRE r.p IS :: STRING")]
    [InlineData("CREATE CONSTRAINT FOR ()-[r:T]-() REQUIRE r.p IS NOT NULL")]
    [InlineData("DROP INDEX ix IF EXISTS")]
    [InlineData("DROP CONSTRAINT c IF EXISTS")]
    [InlineData("DROP INDEX ix")]
    [InlineData("DROP CONSTRAINT c")]
    [InlineData("CREATE LABEL N IF NOT EXISTS")]
    [InlineData("DROP LABEL N IF EXISTS")]
    [InlineData("CREATE RELATIONSHIP TYPE T IF NOT EXISTS")]
    [InlineData("DROP RELATIONSHIP TYPE T IF EXISTS")]
    [InlineData("create index \"a\"\"b\" for (n:\"Odd name\") on (n.\"property name\");")]
    public void Parse_SchemaForm_ShouldRenderSemanticCommand(string text)
    {
        // Arrange
        var parser = new GqlQueryParser();
        // Act
        var parsed = (GqlQueryStatement)parser.Parse(text);
        var rendered = (GqlQueryStatement)parser.Parse(parsed.GqlExpression.ToString());
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        rendered.Diagnostics.ShouldBeEmpty();
        parsed.GqlExpression.Stages.ShouldBeEmpty();
        rendered.GqlExpression.SchemaCommand.ShouldBe(parsed.GqlExpression.SchemaCommand);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Schema DDL: unsupported forms report one named CK00001")]
    [InlineData("CREATE UNIQUE INDEX ix FOR (n:N) ON (n.p)", "UNIQUE INDEX")]
    [InlineData("CREATE INDEX ix FOR (n:N) ON (n.p,n.q)", "COMPOSITE INDEX")]
    [InlineData("CREATE INDEX ix FOR ()-[r:T]-() ON (r.p)", "RELATIONSHIP PROPERTY INDEX")]
    [InlineData("CREATE CONSTRAINT c FOR (n:N) REQUIRE n.p IS UNIQUE", "UNIQUE CONSTRAINT")]
    [InlineData("CREATE CONSTRAINT c FOR (n:N) REQUIRE n.p IS NODE UNIQUE", "UNIQUE CONSTRAINT")]
    [InlineData("CREATE CONSTRAINT c FOR ()-[r:T]-() REQUIRE r.p IS RELATIONSHIP UNIQUE", "UNIQUE CONSTRAINT")]
    [InlineData("CREATE CONSTRAINT c FOR ()-[r:T]-() REQUIRE r.p IS REL UNIQUE", "UNIQUE CONSTRAINT")]
    [InlineData("CREATE CONSTRAINT c FOR (n:N) REQUIRE (n.p,n.q) IS NOT NULL", "COMPOSITE CONSTRAINT")]
    [InlineData("CREATE GRAPH TYPE g", "CLOSED GRAPH TYPE")]
    [InlineData("CREATE CLOSED GRAPH TYPE g", "CLOSED GRAPH TYPE")]
    [InlineData("CREATE CONSTRAINT c FOR (n:N) REQUIRE n.p IS :: DATE", "CONSTRAINT TYPE DATE")]
    public void Parse_TierThreeForm_ShouldNameRefusedConstruct(string text, string form)
    {
        // Arrange / Act
        var parsed = new GqlQueryParser().Parse(text);
        // Assert
        var diagnostic = parsed.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message!.ShouldContain(form, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Schema DDL: malformed commands refuse without ON function noise")]
    [InlineData("DROP INDEX", "CG00002")]
    [InlineData("DROP CONSTRAINT IF EXISTS", "CG00002")]
    [InlineData("CREATE INDEX ix FOR (n:N) ON n.p", "CG00002")]
    [InlineData("CREATE INDEX ix FOR (n:N) ON (m.p)", "CG01022")]
    [InlineData("CREATE CONSTRAINT FOR (n:N) REQUIRE n.p IS NOT NULL RETURN n", "CG00002")]
    public void Parse_InvalidForm_ShouldReturnLocatedDiagnostic(string text, string code)
    {
        // Arrange / Act
        var parsed = new GqlQueryParser().Parse(text);
        // Assert
        var diagnostic = parsed.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldNotBeNull();
    }
}
