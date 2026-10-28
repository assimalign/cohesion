using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlSubqueryParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - EXISTS: precedence follows the SQL++ boundaries")]
    [InlineData("EXISTS x IS NULL", "EXISTS", "IS NULL")]
    [InlineData("EXISTS x IS NOT MISSING", "EXISTS", "IS NOT MISSING")]
    [InlineData("EXISTS x IS VALUED", "EXISTS", "IS VALUED")]
    [InlineData("EXISTS x IS DISTINCT FROM y", "EXISTS", "IS DISTINCT FROM")]
    [InlineData("EXISTS x IS NOT DISTINCT FROM y", "EXISTS", "IS NOT DISTINCT FROM")]
    [InlineData("EXISTS x || y", "EXISTS", "CONCAT")]
    [InlineData("EXISTS x + y", "EXISTS", "+")]
    [InlineData("EXISTS x - y", "EXISTS", "-")]
    [InlineData("EXISTS x * y", "EXISTS", "*")]
    [InlineData("EXISTS x / y", "EXISTS", "/")]
    [InlineData("EXISTS x % y", "EXISTS", "%")]
    [InlineData("EXISTS x = TRUE", "=", "EXISTS")]
    [InlineData("EXISTS x != TRUE", "!=", "EXISTS")]
    [InlineData("EXISTS x < TRUE", "<", "EXISTS")]
    [InlineData("EXISTS x <= TRUE", "<=", "EXISTS")]
    [InlineData("EXISTS x > TRUE", ">", "EXISTS")]
    [InlineData("EXISTS x >= TRUE", ">=", "EXISTS")]
    [InlineData("EXISTS x IN [TRUE]", "IN", "EXISTS")]
    [InlineData("EXISTS x BETWEEN FALSE AND TRUE", "BETWEEN", "EXISTS")]
    [InlineData("EXISTS x LIKE 'a'", "LIKE", "EXISTS")]
    [InlineData("NOT EXISTS x", "NOT", "EXISTS")]
    [InlineData("EXISTS x AND y", "AND", "EXISTS")]
    [InlineData("EXISTS x OR y", "OR", "EXISTS")]
    [InlineData("NOT EXISTS x AND y", "AND", "NOT")]
    public void Parse_ExistsPrecedence_ShouldRetainExpectedOperandTree(string expression, string root, string child)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT " + expression + " FROM c");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var node = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections.Single().Expression;
        Operation(node).ShouldBe(root);
        var operand = node switch
        {
            OqlUnaryExpression unary => unary.Operand,
            OqlBinaryExpression binary => binary.Left,
            OqlInExpression membership => membership.Operand,
            OqlBetweenExpression between => between.Operand,
            OqlLikeExpression like => like.Operand,
            _ => throw new InvalidOperationException(),
        };
        Operation(operand).ShouldBe(child);
        if (expression == "NOT EXISTS x AND y")
        { Operation(operand.ShouldBeOfType<OqlUnaryExpression>().Operand).ShouldBe("EXISTS"); }

        static string Operation(OqlExpression node) => node switch
        {
            OqlUnaryExpression unary => unary.Operator,
            OqlBinaryExpression binary => binary.Operator,
            OqlCallExpression call => call.Name,
            OqlInExpression => "IN", OqlBetweenExpression => "BETWEEN", OqlLikeExpression => "LIKE",
            _ => node.GetType().Name,
        };
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - EXISTS: deferred operator spellings retain their parser refusals")]
    [InlineData("EXISTS a WITHIN b", "CK00001")]
    public void Parse_DeferredExistsBoundary_ShouldPreserveRefusal(string expression, string code)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse("SELECT " + expression + " FROM c");
        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - EXISTS: long membership chains remain expressions")]
    public void Parse_ExistsMembershipChain_ShouldParseWithoutBindingRefusal()
    {
        // Arrange
        string text = "SELECT " + string.Concat(Enumerable.Repeat("EXISTS x IN ", 1024)) + "[TRUE] FROM c";
        // Act
        var statement = new OqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Subqueries: SELECT expressions compose with SQL++ operators")]
    [InlineData("SELECT 1 IS DISTINCT FROM (SELECT RAW n FROM d) FROM c")]
    [InlineData("SELECT ARRAY x IS DISTINCT FROM (SELECT RAW n FROM d) FOR x IN [1] END AS FIRST FROM c")]
    [InlineData("SELECT (SELECT RAW n FROM d) AS a FROM c")]
    [InlineData("SELECT (SELECT RAW n FROM d) AS UNIQUE FROM c")]
    [InlineData("SELECT (SELECT RAW (SELECT RAW n FROM d) FROM d), 1 AS FIRST FROM c")]
    [InlineData("SELECT (SELECT n AS UNIQUE FROM d) AS LAST FROM c")]
    [InlineData("SELECT (SELECT RAW n FROM d) AS SELECT FROM c")]
    [InlineData("SELECT (SELECT n FROM d) FROM c WHERE EXISTS (SELECT RAW n FROM d)")]
    [InlineData("SELECT n FROM c WHERE n NOT IN (SELECT RAW n FROM d)")]
    [InlineData("SELECT n FROM c WHERE n IN (SELECT VALUE n FROM d)")]
    [InlineData("SELECT ARRAY (SELECT RAW n FROM d) FOR x IN [1] END FROM c")]
    [InlineData("SELECT EVERY x IN [1] SATISFIES EXISTS (SELECT RAW n FROM d) END FROM c")]
    [InlineData("SELECT COALESCE((SELECT RAW n FROM d), []) FROM c")]
    [InlineData("SELECT CASE WHEN EXISTS [] THEN (SELECT n FROM d) ELSE (SELECT RAW n FROM d) END FROM c")]
    [InlineData("SELECT COUNT(*) FROM c GROUP BY (SELECT n FROM d) HAVING EXISTS (SELECT RAW n FROM d) ORDER BY (SELECT RAW n FROM d)")]
    [InlineData("SELECT (SELECT DISTINCT RAW n FROM d) FROM c")]
    [InlineData("SELECT (SELECT RAW (SELECT RAW n FROM d) FROM d) FROM c")]
    public void Parse_SubqueryExpressions_ShouldRetainTextWithoutDiagnostics(string text)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.OqlExpression.Text.ShouldBe(text);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Subqueries: AST retains SELECT and parenthesized span")]
    public void Parse_Subquery_ShouldRetainNestedSelectAndSpan()
    {
        // Arrange
        const string text = "SELECT (SELECT RAW n FROM d) FROM c";
        // Act
        var select = ((OqlQueryStatement)new OqlQueryParser().Parse(text)).OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        var subquery = select.Projections.Single().Expression.ShouldBeOfType<OqlSubqueryExpression>();
        // Assert
        subquery.Query.IsValue.ShouldBeTrue();
        subquery.Query.Collection.ShouldBe("d");
        subquery.Location!.Start.ShouldBe(7);
        subquery.Location.End.ShouldBe(text.IndexOf(')') + 1);
        Should.Throw<ArgumentNullException>(() => new OqlSubqueryExpression(null!));
        const string membership = "SELECT n IN (SELECT RAW n FROM d) FROM c";
        var memberSelect = ((OqlQueryStatement)new OqlQueryParser().Parse(membership)).OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        var inner = memberSelect.Projections.Single().Expression.ShouldBeOfType<OqlInExpression>().Array.ShouldBeOfType<OqlSubqueryExpression>();
        inner.Location!.Start.ShouldBe(membership.IndexOf('('));
        inner.Location.End.ShouldBe(membership.IndexOf(')') + 1);
    }
}
