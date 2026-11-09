using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlWindowParserTests
{
    [Theory(DisplayName = "Cohesion Test [OQL] - Windows: contextual names preserve window-free implicit aliases")]
    [InlineData("SELECT window.v FROM c window")]
    [InlineData("SELECT window FROM c UNNEST c.values window")]
    [InlineData("SELECT window FROM c GROUP BY v window")]
    public void Parse_ContextualWindowAlias_ShouldPreserveExistingNames(string text)
    {
        // Arrange / Act / Assert
        Parse(text).Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Windows: ranking navigation and named copies retain their AST")]
    [InlineData("ROW_NUMBER() OVER ()")]
    [InlineData("RANK() OVER (PARTITION BY c.p ORDER BY c.o DESC NULLS FIRST)")]
    [InlineData("DENSE_RANK() OVER w")]
    [InlineData("PERCENT_RANK() OVER (w)")]
    [InlineData("CUME_DIST() OVER (base ORDER BY c.o)")]
    [InlineData("NTILE(7) OVER w")]
    [InlineData("LAG(c.v,0,NULL) OVER w")]
    [InlineData("LEAD(c.v,9223372036854775807,c.d) OVER w")]
    public void Parse_WindowCall_ShouldRetainSpecification(string call)
    {
        // Arrange / Act
        var statement = Parse("SELECT " + call + " AS n FROM c WINDOW base AS (PARTITION BY c.p), w AS (base ORDER BY c.o) ORDER BY n LIMIT 1");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var query = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        query.Projections[0].Expression.ShouldBeOfType<OqlWindowExpression>();
        query.Windows.Count.ShouldBe(2);
        query.Windows[1].Specification.Reference.ShouldBe("base");
        query.Limit.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Windows: illegal positions and deferred forms have one located refusal")]
    [InlineData("SELECT x FROM c WHERE ROW_NUMBER() OVER () = 1", "ROW_NUMBER")]
    [InlineData("SELECT x FROM c GROUP BY RANK() OVER ()", "RANK")]
    [InlineData("SELECT COUNT(*) FROM c HAVING RANK() OVER () = 1", "RANK")]
    [InlineData("SELECT SUM(ROW_NUMBER() OVER ()) FROM c", "ROW_NUMBER")]
    [InlineData("SELECT LAG(RANK() OVER ()) OVER () FROM c", "RANK")]
    [InlineData("SELECT RANK() OVER (ORDER BY ROW_NUMBER() OVER ()) FROM c", "ROW_NUMBER")]
    [InlineData("SELECT ANY x IN [1] SATISFIES RANK() OVER () END FROM c", "RANK")]
    [InlineData("UPDATE c SET x = ROW_NUMBER() OVER ()", "ROW_NUMBER")]
    [InlineData("DELETE FROM c RETURNING RANK() OVER ()", "RANK")]
    [InlineData("MERGE INTO c USING s ON KEY s.k WHEN MATCHED THEN UPDATE SET x = RANK() OVER ()", "RANK")]
    [InlineData("SELECT SUM(x) OVER () FROM c", "SUM")]
    [InlineData("SELECT RANK() OVER (ORDER BY x ROWS UNBOUNDED PRECEDING) FROM c", "ROWS")]
    [InlineData("SELECT RANK() OVER (ORDER BY x RANGE CURRENT ROW) FROM c", "RANGE")]
    [InlineData("SELECT RANK() OVER (ORDER BY x GROUPS CURRENT ROW) FROM c", "GROUPS")]
    [InlineData("SELECT RANK() OVER (ORDER BY x EXCLUDE TIES) FROM c", "EXCLUDE")]
    [InlineData("SELECT FIRST_VALUE(x) FROM LAST IGNORE NULLS OVER () FROM c", "FIRST_VALUE")]
    [InlineData("SELECT LAST_VALUE(x) OVER () FROM c", "LAST_VALUE")]
    [InlineData("SELECT NTH_VALUE(x,2) OVER () FROM c", "NTH_VALUE")]
    [InlineData("SELECT RATIO_TO_REPORT(x) OVER () FROM c", "RATIO_TO_REPORT")]
    [InlineData("SELECT LAG(x) RESPECT NULLS OVER () FROM c", "RESPECT")]
    [InlineData("SELECT LEAD(x) IGNORE NULLS OVER () FROM c", "IGNORE")]
    [InlineData("SELECT ROW_NUMBER() FROM c", "ROW_NUMBER")]
    public void Parse_UnsupportedWindow_ShouldLocateOneRefusal(string text, string marker)
    {
        // Arrange / Act
        var diagnostic = Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Start.ShouldBe(text.IndexOf(marker, StringComparison.Ordinal));
        diagnostic.End!.Value.ShouldBeGreaterThan(diagnostic.Start!.Value);
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - Windows: subqueries own their placement and metadata lists are immutable")]
    public void Parse_SubqueryWindows_ShouldUseLocalScope()
    {
        // Arrange / Act
        var statement = Parse("SELECT x FROM c WHERE EXISTS (SELECT RAW RANK() OVER w FROM [1] AS x WINDOW w AS ())");
        var keys = new[] { new OqlLiteralExpression(1) };
        var specification = new OqlWindowSpecification(keys, []);
        keys[0] = null!;
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        specification.PartitionBy.ShouldHaveSingleItem().ShouldNotBeNull();
        OqlFunctions.All.Count(f => f.WindowKind != OqlWindowFunctionKind.None).ShouldBe(8);
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - Windows: SELECT copies retain immutable window and group declarations")]
    public void Construct_WindowDeclarations_ShouldSnapshotAndPreserveGrouping()
    {
        // Arrange
        var key = new OqlLiteralExpression(1);
        var windows = new[] { new OqlWindowDefinition("w", new OqlWindowSpecification([key], [])) };
        var query = new OqlSelectExpression("items", null, [new OqlProjection(key, "n")], null, [key], null, [], false, false,
            groupAliases: ["g"], groupAs: "members", windows: windows);
        // Act
        windows[0] = null!;
        var copy = new OqlSelectExpression(query.Collection, query.Alias, query.Projections, query.Predicate,
            query.GroupBy, query.Having, query.OrderBy, false, false);
        // Assert
        copy.Windows.ShouldHaveSingleItem().Name.ShouldBe("w");
        copy.GroupAliases.ShouldBe(new[] { "g" });
        copy.GroupAs.ShouldBe("members");
        copy.GroupBy.ShouldHaveSingleItem().ShouldBeSameAs(key);
        foreach (bool both in new[] { false, true })
        {
            var invalid = new OqlWindowExpression(new OqlCallExpression("RANK", []), both ? new OqlWindowSpecification([], []) : null, both ? "w" : null);
            var source = new OqlSelectExpression("", "x", [new OqlProjection(invalid, null)], null, [], null, [], false, true, source: new OqlArrayExpression([]));
            var merge = new OqlMergeExpression("target", null, new OqlMergeSource("", "s", new OqlSubqueryExpression(source)),
                new OqlLiteralExpression("key"), true, delete: new OqlMergeDeleteAction());
            Should.Throw<InvalidOperationException>(() => merge.ToString());
        }
    }

    private static OqlQueryStatement Parse(string text) => (OqlQueryStatement)new OqlQueryParser().Parse(text);
}
