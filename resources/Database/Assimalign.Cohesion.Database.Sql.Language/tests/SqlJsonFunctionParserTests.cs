using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlJsonFunctionParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - SQL/JSON: quoted names retain ordinary function syntax (#1722)")]
    [InlineData("json_value")]
    [InlineData("JSON_QUERY")]
    [InlineData("json_exists")]
    public void Render_QuotedJsonNames_ShouldPreserveOrdinaryCalls(string name)
    {
        // Arrange / Act: PostgreSQL gram.y:16017-16027,16576-16633 distinguishes keywords from identifiers.
        var expression = Parse("\"" + name + "\"(doc,'$.a')");
        // Assert
        expression.ShouldBeOfType<SqlFunctionCallExpression>();
        string rendered = SqlExpressionRenderer.Render(expression);
        rendered.ShouldStartWith("\"" + name + "\"(");
        Parse(rendered).ShouldBeOfType<SqlFunctionCallExpression>();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - SQL/JSON: clauses and effective defaults round trip (#1722)")]
    [InlineData("JSON_VALUE(doc, '$.a')")]
    [InlineData("JSON_VALUE(doc, @p RETURNING NUMERIC(6, 2) DEFAULT 12 ON EMPTY DEFAULT 13 ON ERROR)")]
    [InlineData("JSON_QUERY(doc, '$[*]' RETURNING JSONB WITH ARRAY WRAPPER EMPTY ARRAY ON EMPTY EMPTY OBJECT ON ERROR)")]
    [InlineData("JSON_QUERY(doc, '$' WITH CONDITIONAL WRAPPER NULL ON EMPTY ERROR ON ERROR)")]
    [InlineData("JSON_QUERY(doc, '$' WITHOUT WRAPPER OMIT QUOTES ON SCALAR STRING)")]
    [InlineData("JSON_QUERY(doc, '$' KEEP QUOTES DEFAULT '{}'::JSONB ON EMPTY)")]
    [InlineData("JSON_EXISTS(doc, '$.a' UNKNOWN ON ERROR)")]
    public void Render_JsonClauses_ShouldPreserveStructure(string source)
    {
        // Arrange
        var expression = Parse(source);
        // Act
        string rendered = SqlExpressionRenderer.Render(expression);
        // Assert
        SqlTreeDump.Dump(Parse(rendered)).ShouldBe(SqlTreeDump.Dump(expression));
        SqlExpressionRenderer.Render(Parse(rendered)).ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - SQL/JSON: invalid clause combinations refuse (#1722)")]
    [InlineData("JSON_VALUE(doc, '$' TRUE ON ERROR)")]
    [InlineData("JSON_VALUE(doc, '$' NULL ON ERROR NULL ON EMPTY)")]
    [InlineData("JSON_QUERY(doc, '$' WITH WRAPPER OMIT QUOTES)")]
    [InlineData("JSON_QUERY(doc, '$' EMPTY ARRAY ON EMPTY EMPTY OBJECT ON EMPTY)")]
    [InlineData("JSON_EXISTS(doc, '$' NULL ON ERROR)")]
    [InlineData("JSON_EXISTS(doc, '$' TRUE ON EMPTY)")]
    [InlineData("JSON_EXISTS(doc, '$' RETURNING INT)")]
    [InlineData("JSON_QUERY(doc, '$' PASSING 1 AS x)")]
    public void Parse_InvalidJsonClauses_ShouldRefuse(string source)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("SELECT " + source + ";");
        // Assert
        statement.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - SQL/JSON: PASSING produces one located capability refusal (#1722)")]
    public void Parse_Passing_ShouldRefuseOnce()
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("SELECT JSON_QUERY(doc,'$' PASSING COALESCE(1,2) AS x);");
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02081");
    }

    private static SqlExpression Parse(string source)
    {
        var statement = new SqlQueryParser().Parse("SELECT " + source + ";").ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message).ShouldBeEmpty();
        return statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression;
    }
}
