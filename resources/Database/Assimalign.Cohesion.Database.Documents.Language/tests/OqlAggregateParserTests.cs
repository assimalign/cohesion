using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlAggregateParserTests
{
    public static IEnumerable<object[]> Calls =>
        from name in new[] { "COUNT", "SUM", "AVG", "MIN", "MAX", "ARRAY_AGG", "BOOL_AND", "BOOL_OR", "EVERY", "STDDEV", "VARIANCE" }
        from quantifier in Enum.GetValues<OqlAggregateQuantifier>()
        select new object[] { name, quantifier };

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Aggregates: call quantifiers and native metadata survive parsing")]
    [MemberData(nameof(Calls))]
    public void Parse_AggregateQuantifier_ShouldRetainCallMetadata(string name, OqlAggregateQuantifier quantifier)
    {
        // Arrange
        string text = $"SELECT {name}({(quantifier == OqlAggregateQuantifier.None ? "" : quantifier + " ")}x) FROM c";
        // Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var call = ((OqlSelectExpression)statement.OqlExpression).Projections[0].Expression.ShouldBeOfType<OqlCallExpression>();
        call.Name.ShouldBe(name);
        call.Quantifier.ShouldBe(quantifier);
        call.Arguments.ShouldHaveSingleItem().ShouldBeOfType<OqlPathExpression>();
        OqlFunctions.Find(name)!.IsAggregate.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Aggregates: invalid quantifiers have one located syntax diagnostic")]
    [InlineData("COUNT(DISTINCT *)", "CD00011")]
    [InlineData("COUNT(ALL *)", "CD00011")]
    [InlineData("LOWER(DISTINCT x)", "CD00011")]
    [InlineData("META(ALL x).id", "CD00011")]
    [InlineData("COUNT(DISTINCT)", "CD00006")]
    [InlineData("SUM(DISTINCT x, x)", "CD00006")]
    [InlineData("COUNT(DISTINCT ALL x)", "CD00002")]
    [InlineData("BOOL_AND(*)", "CD00006")]
    [InlineData("STDDEV()", "CD00006")]
    public void Parse_InvalidAggregateQuantifier_ShouldRefuseOnce(string expression, string code)
    {
        // Arrange / Act
        var diagnostic = new OqlQueryParser().Parse($"SELECT {expression} FROM c").Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldNotBeNull();
        diagnostic.End.ShouldNotBeNull();
        diagnostic.Line.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Aggregates: EVERY call and quantified predicate remain separate nodes")]
    [InlineData("")]
    [InlineData(" GROUP BY flag")]
    public void Parse_EveryCallAndPredicate_ShouldKeepDistinctNodes(string grouping)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(
            "SELECT EVERY(EVERY x IN tags SATISFIES x END) AS a, EVERY(flag) AS b FROM c" + grouping);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var select = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        select.Projections[0].Expression.ShouldBeOfType<OqlCallExpression>().Arguments[0].ShouldBeOfType<OqlQuantifiedExpression>().IsEvery.ShouldBeTrue();
        select.Projections[1].Expression.ShouldBeOfType<OqlCallExpression>().Name.ShouldBe("EVERY");
        new OqlQueryParser().Parse("SELECT every, bool_and, stddev, array_agg FROM c every").Diagnostics.ShouldBeEmpty();
    }
}
