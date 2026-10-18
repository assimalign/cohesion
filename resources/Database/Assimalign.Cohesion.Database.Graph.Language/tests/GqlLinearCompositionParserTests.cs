using System;
using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlLinearCompositionParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Linear composition: clauses retain their ordered stage nodes")]
    [InlineData("LET x = 1, y = 'k' RETURN x, y", "Let,Return")]
    [InlineData("MATCH (a) LET n = a.name RETURN n", "Match,Let,Return")]
    [InlineData("MATCH (a) FILTER a.age > 17 RETURN a", "Match,Filter,Return")]
    [InlineData("MATCH (a) FILTER WHERE a.age > 17 RETURN a", "Match,Filter,Return")]
    [InlineData("FOR x IN $xs RETURN x", "For,Return")]
    [InlineData("UNWIND $xs AS x RETURN x", "For,Return")]
    [InlineData("UNWIND [1, 2] AS a RETURN a", "For,Return")]
    [InlineData("MATCH (a) RETURN a NEXT MATCH (a)-[:KNOWS]->(b) RETURN b.name", "Match,Return,Next,Match,Return")]
    [InlineData("MATCH (a) WITH a WHERE a.age > 17 RETURN a.name", "Match,Return,Return")]
    [InlineData("MATCH (a) WITH DISTINCT a.active AS act RETURN act", "Match,Return,Return")]
    [InlineData("MATCH (a)-[r]->(b) WITH * RETURN b.name", "Match,Return,Return")]
    [InlineData("INSERT (:T {k: 2}) MATCH (t:T) RETURN t.k", "Insert,Match,Return")]
    [InlineData("INSERT (a) MATCH (b) RETURN b", "Insert,Match,Return")]
    [InlineData("MATCH (a) DELETE a MATCH (b) RETURN b", "Match,Delete,Match,Return")]
    [InlineData("FOR x IN $xs WITH x RETURN x", "For,Return,Return")]
    [InlineData("LET x = 1 RETURN x AS next NEXT RETURN next AS for", "Let,Return,Next,Return")]
    [InlineData("LET for = 1, next = 2 RETURN for, next", "Let,Return")]
    [InlineData("MATCH (for {next: 1, for: 2}) RETURN for.next AS next", "Match,Return")]
    [InlineData("MATCH (next) FILTER next:Order RETURN next", "Match,Filter,Return")]
    [InlineData("EXPLAIN LET x = 1 RETURN x", "Let,Return")]
    public void Parse_LinearStatement_ShouldPreserveStageOrder(string source, string expected)
    {
        // Arrange / Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var query = statement.GqlExpression;
        string.Join(",", query.Stages.Select(stage => stage.GetType().Name.Replace("Gql", "", StringComparison.Ordinal).Replace("Stage", "", StringComparison.Ordinal))).ShouldBe(expected);
        query.Segments.Count.ShouldBe(query.Stages.Count(stage => stage is GqlNextStage) + 1);
        query.Segments.SelectMany(segment => segment.Stages).ShouldBe(query.Stages.Where(stage => stage is not GqlNextStage));
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Linear composition: excluded and malformed forms retain precise diagnostics")]
    [InlineData("FOR x IN $xs WITH ORDINALITY i RETURN x, i", "COHDBL001", "FOR ORDINALITY/OFFSET SUFFIX")]
    [InlineData("FOR x IN $xs WITH OFFSET i RETURN x, i", "COHDBL001", "FOR ORDINALITY/OFFSET SUFFIX")]
    [InlineData("LET next = abs(1) RETURN next", "COHDBL001", "FUNCTION abs")]
    [InlineData("LET next=1 LET x=next + abs(1) RETURN x", "COHDBL001", "FUNCTION abs")]
    [InlineData("LET x = abs(1) RETURN x", "COHDBL001", "FUNCTION abs")]
    [InlineData("FOR x IN abs(1) RETURN x", "COHDBL001", "FUNCTION abs")]
    [InlineData("UNWIND abs(1) AS x RETURN x", "COHDBL001", "FUNCTION abs")]
    [InlineData("FILTER abs(1) = 1 RETURN 1", "COHDBL001", "FUNCTION abs")]
    [InlineData("WITH abs(1) AS x RETURN x", "COHDBL001", "FUNCTION abs")]
    [InlineData("RETURN 1 NEXT YIELD x", "COHDBL001", "YIELD")]
    [InlineData("FOREACH (x IN [1] | INSERT (:T))", "COHDBL001", "FOREACH")]
    [InlineData("CALL foo()", "COHDBL001", "CALL")]
    [InlineData("LET x = 1 ORDER BY x RETURN x", "COHDBL001", "STANDALONE ORDER BY AND PAGE STATEMENT")]
    [InlineData("FOR x IN [1] LIMIT 1 RETURN x", "COHDBL001", "STANDALONE ORDER BY AND PAGE STATEMENT")]
    [InlineData("FILTER TRUE OFFSET 1 RETURN 1", "COHDBL001", "STANDALONE ORDER BY AND PAGE STATEMENT")]
    [InlineData("MATCH (a) NEXT MATCH (b) RETURN b", "GQL0002", "NEXT")]
    [InlineData("RETURN 1 NEXT", "GQL0002", "NEXT")]
    [InlineData("MATCH (a) DELETE a RETURN a", "COHDBL001", "RETURN AFTER DELETE")]
    [InlineData("RETURN 1 NEXT RETURN 2 UNION RETURN 3", "COHDBL001", "UNION")]
    public void Parse_ExcludedForm_ShouldReportOneDiagnostic(string source, string code, string message)
    {
        // Arrange / Act
        var diagnostic = new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldNotBeNull().ShouldContain(message, Case.Sensitive);
    }
}
