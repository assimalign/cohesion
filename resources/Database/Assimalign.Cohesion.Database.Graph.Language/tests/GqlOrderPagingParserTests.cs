using System;
using System.Linq;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlOrderPagingParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Ordering and paging: supported tails parse")]
    [InlineData("MATCH (a) RETURN a ORDER BY a.age DESC NULLS LAST, a.name")]
    [InlineData("MATCH (a) RETURN a OFFSET 1 LIMIT 2")]
    [InlineData("MATCH (a) RETURN a SKIP 1 LIMIT 2")]
    [InlineData("RETURN 1 LIMIT 0")]
    [InlineData("MATCH (a) RETURN a.age AS age ORDER BY age")]
    [InlineData("MATCH (a) RETURN a.name ORDER BY a.age + 1")]
    [InlineData("MATCH (first:Person) RETURN first.name ORDER BY first.name NULLS LAST")]
    [InlineData("MATCH (last:Person) RETURN last.name ORDER BY last.name NULLS FIRST")]
    [InlineData("MATCH (a) RETURN a.name ORDER BY a.name OFFSET @start LIMIT $rows")]
    [InlineData("MATCH (a) RETURN a ORDER BY a.age ASCENDING, a.name DESCENDING")]
    [InlineData("MATCH (a) RETURN a SKIP $start LIMIT @rows")]
    [InlineData("RETURN CASE WHEN TRUE THEN 1 END AS x ORDER BY COALESCE(x, 0)")]
    public void Parse_OrderPagingTail_ShouldAccept(string source)
    {
        // Arrange
        var parser = new GqlQueryParser();
        // Act
        var result = (GqlQueryStatement)parser.Parse(source);
        // Assert
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Ordering and paging: AST retains direction, explicit nulls and parameter names")]
    public void Parse_OrderPagingTail_ShouldRetainAst()
    {
        // Arrange
        const string source = "MATCH (a) RETURN a ORDER BY a.age DESC NULLS LAST, a.name OFFSET @start LIMIT $rows";
        // Act
        var result = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        // Assert
        result.Diagnostics.ShouldBeEmpty();
        var query = result.GqlExpression;
        query.OrderBy.Count.ShouldBe(2);
        query.OrderBy[0].Direction.ShouldBe(GqlSortDirection.Descending);
        query.OrderBy[0].NullOrdering.ShouldBe(GqlNullOrdering.Last);
        query.OrderBy[1].Direction.ShouldBe(GqlSortDirection.Ascending);
        query.OrderBy[1].NullOrdering.ShouldBe(GqlNullOrdering.Default);
        query.Offset.ShouldBeOfType<GqlParameterExpression>().Name.ShouldBe("start");
        query.Limit.ShouldBeOfType<GqlParameterExpression>().Name.ShouldBe("rows");
        GqlLanguageProfile.Instance.Keywords.ToArray().ShouldNotContain(word => word == "NULLS" || word == "FIRST" || word == "LAST");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Ordering and paging: malformed tails retain diagnostic and offending span")]
    [InlineData("RETURN 1 LIMIT -1", "GQL0002", "-", 1)]
    [InlineData("RETURN 1 LIMIT 1.5", "GQL0002", "1.5", 3)]
    [InlineData("RETURN 1 LIMIT 'x'", "GQL0002", "'x'", 3)]
    [InlineData("RETURN 1 LIMIT 9223372036854775808", "GQL0004", "9223372036854775808", 19)]
    [InlineData("RETURN 1 OFFSET 1 OFFSET 2", "GQL0002", "OFFSET 2", 6)]
    [InlineData("RETURN 1 SKIP 1 OFFSET 1", "GQL0002", "OFFSET", 6)]
    [InlineData("RETURN 1 LIMIT 2 OFFSET 1", "GQL0002", "OFFSET", 6)]
    [InlineData("RETURN 1 LIMIT 2 SKIP 1", "GQL0002", "SKIP", 4)]
    [InlineData("MATCH (a) RETURN a.name LIMIT 1 ORDER BY a.age", "GQL0002", "ORDER", 5)]
    [InlineData("MATCH (a) RETURN a ORDER BY a.age NULLS MIDDLE", "GQL0002", "MIDDLE", 6)]
    [InlineData("MATCH (a) ORDER BY a.age RETURN a", "COHDBL001", "ORDER", 5)]
    [InlineData("MATCH (a) LIMIT 1 RETURN a", "COHDBL001", "LIMIT", 5)]
    [InlineData("ORDER BY 1", "COHDBL001", "ORDER", 5)]
    [InlineData("OFFSET 1", "COHDBL001", "OFFSET", 6)]
    [InlineData("SKIP 1", "COHDBL001", "SKIP", 4)]
    [InlineData("LIMIT 1", "COHDBL001", "LIMIT", 5)]
    [InlineData("INSERT (a:Person) ORDER BY a.age RETURN a", "COHDBL001", "ORDER", 5)]
    [InlineData("INSERT (a:Person) LIMIT 1 RETURN a", "COHDBL001", "LIMIT", 5)]
    [InlineData("CREATE (a:Person) SKIP 1 RETURN a", "COHDBL001", "SKIP", 4)]
    [InlineData("MATCH (a) DELETE a OFFSET 1", "COHDBL001", "OFFSET", 6)]
    [InlineData("MATCH (a) DETACH DELETE a ORDER BY a.age", "COHDBL001", "ORDER", 5)]
    [InlineData("RETURN 1 LIMIT :rows", "GQL0002", ":rows", 5)]
    [InlineData("RETURN 1 SKIP ?", "GQL0002", "?", 1)]
    public void Parse_InvalidTail_ShouldReportOffendingToken(string source, string code, string offending, int length)
    {
        // Arrange
        var parser = new GqlQueryParser();
        // Act
        var result = (GqlQueryStatement)parser.Parse(source);
        // Assert
        var diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(source.IndexOf(offending, StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + length);
        if (code == "COHDBL001") { diagnostic.Message!.ShouldContain("STANDALONE ORDER BY AND PAGE STATEMENT"); }
        if (offending is ":rows" or "?")
        {
            diagnostic.Message.ShouldBe(QueryDiagnostics.UnsupportedParameterForm(diagnostic.Code!,
                Location.Create(1, 1, diagnostic.Start!.Value, diagnostic.End!.Value)).Message);
            diagnostic.Message!.ShouldContain("$name");
            diagnostic.Message!.ShouldContain("@name");
        }
    }
}
