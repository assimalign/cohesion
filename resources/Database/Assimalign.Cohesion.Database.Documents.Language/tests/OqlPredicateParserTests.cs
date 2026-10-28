using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlPredicateParserTests
{
    [Theory(DisplayName = "Cohesion Test [OQL] - Predicates: accepted forms retain typed operands")]
    [InlineData("a IN (1, 'x', $p)", "in", false)]
    [InlineData("a NOT IN (1)", "in", true)]
    [InlineData("a NOT LIKE 'x!%%' ESCAPE '!'", "like", true)]
    [InlineData("a LIKE 'x\\%'", "like", false)]
    [InlineData("a LIKE 'x' ESCAPE ''", "like", false)]
    [InlineData("CASE WHEN a > 1 THEN 'hi' WHEN a IS NULL THEN 'none' ELSE 'lo' END", "case", false)]
    [InlineData("CASE a WHEN 1 THEN 'one' END", "case", false)]
    [InlineData("SUM(CASE WHEN a > 1 THEN 1 ELSE 0 END)", "sum", false)]
    public void Parse_PredicateCorpus_ShouldRetainAst(string expression, string kind, bool negated)
    {
        // Arrange / Act
        var parsed = Parse($"SELECT {expression} FROM c");
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        var node = parsed.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections.Single().Expression;
        node.Location!.Start.ShouldBe(7);
        node.Location.End.ShouldBe(7 + expression.Length);
        switch (kind)
        {
            case "in":
                var membership = node.ShouldBeOfType<OqlInExpression>();
                membership.Negated.ShouldBe(negated);
                membership.Operand.ShouldBeOfType<OqlPathExpression>();
                membership.Values.Count.ShouldBe(negated ? 1 : 3);
                if (!negated) { membership.Values[2].ShouldBeOfType<OqlParameterExpression>().Name.ShouldBe("p"); }
                break;
            case "like":
                var like = node.ShouldBeOfType<OqlLikeExpression>();
                like.Negated.ShouldBe(negated);
                like.Pattern.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<string>();
                if (expression.Contains("ESCAPE", StringComparison.Ordinal))
                {
                    like.Escape.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBe(negated ? "!" : "");
                }
                else { like.Escape.ShouldBeNull(); }
                break;
            case "case":
                var conditional = node.ShouldBeOfType<OqlCaseExpression>();
                conditional.Branches.Count.ShouldBe(expression.StartsWith("CASE WHEN", StringComparison.Ordinal) ? 2 : 1);
                if (conditional.Branches.Count == 1) { conditional.Operand.ShouldBeOfType<OqlPathExpression>(); conditional.Otherwise.ShouldBeNull(); }
                else { conditional.Operand.ShouldBeNull(); conditional.Otherwise.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBe("lo"); }
                break;
            case "sum":
                node.ShouldBeOfType<OqlCallExpression>().Arguments.Single().ShouldBeOfType<OqlCaseExpression>();
                break;
        }
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - BETWEEN: bounds bind above AND and inside prefix NOT")]
    public void Parse_BetweenPrecedence_ShouldKeepBooleanStructure()
    {
        // Arrange / Act
        var parsed = Parse("SELECT a BETWEEN 1 AND 3 AND b = 2, NOT a BETWEEN 1 AND 3, a NOT BETWEEN 1 AND 3 FROM c");
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        var expressions = parsed.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections;
        var conjunction = expressions[0].Expression.ShouldBeOfType<OqlBinaryExpression>();
        conjunction.Operator.ShouldBe("AND");
        var between = conjunction.Left.ShouldBeOfType<OqlBetweenExpression>();
        between.Lower.ShouldBeOfType<OqlLiteralExpression>();
        between.Upper.ShouldBeOfType<OqlLiteralExpression>();
        conjunction.Right.ShouldBeOfType<OqlBinaryExpression>().Operator.ShouldBe("=");
        expressions[1].Expression.ShouldBeOfType<OqlUnaryExpression>().Operand.ShouldBeOfType<OqlBetweenExpression>();
        expressions[2].Expression.ShouldBeOfType<OqlBetweenExpression>().Negated.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Predicates: malformed and deferred forms have exact diagnostic spans")]
    [InlineData("SELECT a IN («)» FROM c", "CD00002", null)]
    [InlineData("SELECT a IN (1,«»", "CD00002", null)]
    [InlineData("SELECT a BETWEEN 1«»", "CD00002", null)]
    [InlineData("SELECT a BETWEEN 1 «OR» 2 FROM c", "CD00002", null)]
    [InlineData("SELECT a LIKE 'x' ESCAPE «'ab'» FROM c", "CD00002", null)]
    [InlineData("SELECT CASE «END» FROM c", "CD00002", null)]
    [InlineData("SELECT CASE WHEN a = 1 THEN 2 «FROM» c", "CD00002", null)]
    [InlineData("SELECT a NOT«»", "CD00002", null)]
    [InlineData("SELECT a IN («SELECT» b FROM d) FROM c", "CD00014", "RAW")]
    public void Parse_InvalidPredicate_ShouldLocateOneDiagnostic(string marked, string code, string? construct)
    {
        // Arrange
        int start = marked.IndexOf('«');
        int end = marked.IndexOf('»') - 1;
        string query = marked.Replace("«", "", StringComparison.Ordinal).Replace("»", "", StringComparison.Ordinal);
        // Act
        var parsed = Parse(query);
        // Assert
        var diagnostic = parsed.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(end);
        if (construct is not null) { diagnostic.Message!.ShouldContain(construct, Case.Sensitive); }
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - CASE reservations: quoted and dotted fields remain reachable")]
    [InlineData("case")]
    [InlineData("when")]
    [InlineData("then")]
    [InlineData("else")]
    [InlineData("end")]
    [InlineData("escape")]
    public void Parse_ReservedField_ShouldRequireQuotesOrDot(string field)
    {
        // Arrange / Act
        var quoted = Parse($"SELECT \"{field}\", d.{field} FROM c d");
        var bare = Parse($"SELECT {field} FROM c");
        // Assert
        quoted.Diagnostics.ShouldBeEmpty();
        bare.Diagnostics.ShouldNotBeEmpty();
    }

    private static OqlQueryStatement Parse(string query) => new OqlQueryParser().Parse(query).ShouldBeOfType<OqlQueryStatement>();
}
