using System;
using System.Linq;
using Assimalign.Cohesion.Database.Language;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlQueryParserTests
{
    [Theory]
    [InlineData("SELECT * FROM people")]
    [InlineData("select * from people;")]
    [InlineData("SELECT p.name FROM people AS p WHERE p.age >= $age")]
    [InlineData("SELECT p.name FROM people p WHERE p.age >= @age")]
    [InlineData("SELECT \"first name\" FROM \"person collection\"")]
    [InlineData("SELECT p.address.city, p.orders[0].total FROM people p")]
    [InlineData("SELECT p['unusual-field'][2] FROM people p")]
    [InlineData("SELECT TRUE, FALSE, NULL, NIL, 'it''s', .5, 1e2 FROM valueset")]
    [InlineData("SELECT -price + 2 * quantity AS adjusted FROM products")]
    [InlineData("SELECT * FROM people WHERE NOT age < 18 AND active = TRUE OR name = 'Ana'")]
    [InlineData("SELECT * FROM people WHERE age IS NULL OR address IS NOT NULL")]
    [InlineData("SELECT * FROM people WHERE age <> 0 AND score != 0")]
    [InlineData("SELECT price / 2 % 3 FROM products")]
    [InlineData("SELECT COUNT(*) FROM people")]
    [InlineData("SELECT SUM(score), AVG(score), MIN(score), MAX(score), COUNT(score) FROM people")]
    [InlineData("SELECT country, COUNT(*) AS total FROM people GROUP BY country HAVING COUNT(*) > 1 ORDER BY total DESC")]
    [InlineData("SELECT region, age FROM people ORDER BY region ASC, age DESC")]
    [InlineData("/* outer /* nested */ comment */ SELECT name -- trailing comment\nFROM people;")]
    [InlineData("SELECT p.flatten, p.select, p.array FROM people p")]
    [InlineData("SELECT 'FLATTEN' AS \"DEFINE\" FROM people /* ELEMENT */")]
    public void ValidCorpus_ProducesExecutableSelect(string query)
    {
        var statement = Parse(query);

        statement.Diagnostics.ShouldBeEmpty();
        statement.OqlExpression.Collection.ShouldNotBeNullOrWhiteSpace();
        statement.OqlExpression.Projections.ShouldNotBeEmpty();
        statement.Expression.ShouldBeSameAs(statement.OqlExpression);
        statement.Expression.Text.ShouldBe(query);
        statement.OqlExpression.Location!.Start.ShouldBe(query.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("", "OQL0001")]
    [InlineData("-- comment", "OQL0001")]
    [InlineData("SELEC name FROM people", "OQL0002")]
    [InlineData("SELECT FROM people", "OQL0002")]
    [InlineData("SELECT name people", "OQL0002")]
    [InlineData("SELECT * FROM", "OQL0002")]
    [InlineData("SELECT * FROM \"\"", "OQL0002")]
    [InlineData("SELECT * FROM people WHERE", "OQL0002")]
    [InlineData("SELECT * FROM people WHERE age =", "OQL0002")]
    [InlineData("SELECT * FROM people WHERE age = 1 AND", "OQL0002")]
    [InlineData("SELECT (name FROM people", "OQL0002")]
    [InlineData("SELECT name, FROM people", "OQL0002")]
    [InlineData("SELECT a. FROM people", "OQL0002")]
    [InlineData("SELECT a[-1] FROM people", "OQL0002")]
    [InlineData("SELECT a[1.5] FROM people", "OQL0002")]
    [InlineData("SELECT a[1 FROM people", "OQL0002")]
    [InlineData("SELECT a[2147483648] FROM people", "OQL0002")]
    [InlineData("SELECT $ FROM people", "OQL0002")]
    [InlineData("SELECT ? FROM people", "OQL0002")]
    [InlineData("SELECT 'unterminated FROM people", "OQL0003")]
    [InlineData("SELECT 'escaped'' FROM people", "OQL0003")]
    [InlineData("SELECT \"unterminated FROM people", "OQL0003")]
    [InlineData("SELECT * FROM people /* unfinished", "OQL0003")]
    [InlineData("SELECT * FROM people /* outer /* inner */", "OQL0003")]
    [InlineData("SELECT 1e FROM people", "OQL0004")]
    [InlineData("SELECT 1e100 FROM people", "OQL0004")]
    [InlineData("SELECT COUNT() FROM people", "OQL0006")]
    [InlineData("SELECT SUM(*) FROM people", "OQL0006")]
    [InlineData("SELECT MAX(a,b) FROM people", "OQL0006")]
    [InlineData("SELECT * FROM people GROUP region", "OQL0002")]
    [InlineData("SELECT * FROM people ORDER BY", "OQL0002")]
    [InlineData("SELECT * FROM people ORDER BY age WHERE age > 0", "OQL0002")]
    [InlineData("SELECT * FROM people; SELECT * FROM other", "OQL0002")]
    [InlineData("SELECT * FROM other.people", "OQL0002")]
    public void MalformedCorpus_ReturnsLocatedDiagnosticsWithoutThrowing(string query, string code)
    {
        var statement = Parse(query);

        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        foreach (var diagnostic in statement.Diagnostics)
        {
            diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
            diagnostic.Location.ShouldBe(DiagnosticLocation.Absolute);
            diagnostic.Start!.Value.ShouldBeInRange(0, query.Length);
            diagnostic.End!.Value.ShouldBeInRange(diagnostic.Start.Value, query.Length);
        }
    }

    [Theory]
    [InlineData("DEFINE people AS SELECT * FROM people", "DEFINE", 0)]
    [InlineData("SELECT ELEMENT(address) FROM people", "ELEMENT", 7)]
    [InlineData("SELECT FLATTEN(address) FROM people", "FLATTEN", 7)]
    [InlineData("SELECT (SELECT city FROM addresses) FROM people", "SUBQUERY", 8)]
    [InlineData("SELECT DISTINCT name FROM people", "DISTINCT", 7)]
    [InlineData("SELECT ABS(age) FROM people", "ABS", 7)]
    [InlineData("SELECT * FROM people LIMIT 10", "LIMIT", 21)]
    [InlineData("SELECT * FROM people WHERE age BETWEEN 1 AND 2", "BETWEEN", 31)]
    [InlineData("CREATE DATABASE other", "CREATE", 0)]
    [InlineData("DROP DATABASE other", "DROP", 0)]
    [InlineData("USE other", "USE", 0)]
    public void UnsupportedCorpus_ReportsCapabilityCodeAndExactToken(string query, string clause, int start)
    {
        var diagnostic = Parse(query).Diagnostics.First(d => d.Code == "COHDBL001");

        diagnostic.Message!.ShouldContain(clause);
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(start + (clause == "SUBQUERY" ? "SELECT".Length : clause.Length));
        diagnostic.Line.ShouldBe(1);
    }

    [Fact]
    public void Diagnostic_OnSecondLine_UsesAbsoluteSpanAndLine()
    {
        const string query = "SELECT name\nFROM people\nWHERE age =";
        var diagnostic = Parse(query).Diagnostics.First();

        diagnostic.Start.ShouldBe(query.Length);
        diagnostic.End.ShouldBe(query.Length);
        diagnostic.Line.ShouldBe(3);

        var unsupported = Parse("SELECT\n  FLATTEN(address) FROM people").Diagnostics.Single();
        unsupported.Start.ShouldBe(9);
        unsupported.End.ShouldBe(16);
        unsupported.Line.ShouldBe(2);
    }

    [Fact]
    public void ExpressionTree_PreservesPathsAliasesAndOrder()
    {
        var select = Parse("SELECT p.orders[2]['total'] AS amount FROM people p WHERE p.age >= $minimum ORDER BY amount DESC").OqlExpression;

        select.Collection.ShouldBe("people");
        select.Alias.ShouldBe("p");
        var projection = select.Projections.ShouldHaveSingleItem();
        projection.Alias.ShouldBe("amount");
        projection.Expression.ShouldBeOfType<OqlPathExpression>().Segments.ShouldBe(new[]
        {
            new OqlPathSegment("p", null), new OqlPathSegment("orders", null),
            new OqlPathSegment(null, 2), new OqlPathSegment("total", null),
        });
        var predicate = select.Predicate.ShouldBeOfType<OqlBinaryExpression>();
        predicate.Operator.ShouldBe(">=");
        predicate.Right.ShouldBeOfType<OqlParameterExpression>().Name.ShouldBe("minimum");
        select.OrderBy.ShouldHaveSingleItem().Descending.ShouldBeTrue();
    }

    [Fact]
    public void ExpressionTree_ObservesArithmeticComparisonAndBooleanPrecedence()
    {
        var select = Parse("SELECT 1 + 2 * 3 FROM people WHERE NOT age < 18 AND active = TRUE OR age = 65").OqlExpression;
        var addition = select.Projections[0].Expression.ShouldBeOfType<OqlBinaryExpression>();
        addition.Operator.ShouldBe("+");
        addition.Right.ShouldBeOfType<OqlBinaryExpression>().Operator.ShouldBe("*");
        var or = select.Predicate.ShouldBeOfType<OqlBinaryExpression>();
        or.Operator.ShouldBe("OR");
        var and = or.Left.ShouldBeOfType<OqlBinaryExpression>();
        and.Operator.ShouldBe("AND");
        var not = and.Left.ShouldBeOfType<OqlUnaryExpression>();
        not.Operator.ShouldBe("NOT");
        not.Operand.ShouldBeOfType<OqlBinaryExpression>().Operator.ShouldBe("<");
    }

    [Fact]
    public void ExpressionTree_PreservesGroupingAggregationAndHaving()
    {
        var select = Parse("SELECT country, COUNT(*) AS n FROM people GROUP BY country HAVING COUNT(*) > 2 ORDER BY n DESC").OqlExpression;

        select.GroupBy.ShouldHaveSingleItem().ShouldBeOfType<OqlPathExpression>().Segments[0].Name.ShouldBe("country");
        var call = select.Projections[1].Expression.ShouldBeOfType<OqlCallExpression>();
        call.Name.ShouldBe("COUNT");
        call.Arguments.ShouldHaveSingleItem().ShouldBeOfType<OqlStarExpression>();
        select.Having.ShouldBeOfType<OqlBinaryExpression>().Right.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBe(2m);
    }

    [Fact]
    public void ExcessiveNesting_ReturnsDiagnosticAndParserRemainsReusable()
    {
        var parser = new OqlQueryParser();
        string query = "SELECT " + new string('(', 256) + "1" + new string(')', 256) + " FROM people";

        parser.Parse(query).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "OQL0005");
        parser.Parse("SELECT * FROM people").Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void LiteralValues_AreCultureIndependentAndUnescaped()
    {
        var expressions = Parse("SELECT 'it''s', 1.25e2, TRUE, NULL FROM people").OqlExpression.Projections;

        expressions[0].Expression.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBe("it's");
        expressions[1].Expression.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBe(125m);
        expressions[2].Expression.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBe(true);
        expressions[3].Expression.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeNull();
    }

    private static OqlQueryStatement Parse(string query) => new OqlQueryParser().Parse(query).ShouldBeOfType<OqlQueryStatement>();
}
