using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlMergeParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - MERGE retains and renders whole paths and SET branches (#1735)")]
    [InlineData("MERGE (n:P {id:$id})")]
    [InlineData("MERGE (on:P) ON CREATE SET on.x=1 ON MATCH SET on.x=2")]
    [InlineData("MERGE (n:P) ON CREATE SET n.x=1, n:New ON MATCH SET n += {x:2}")]
    [InlineData("MERGE (n:P)-[r:R]-(m:P) ON MATCH SET r.x=2 ON CREATE SET r.x=1")]
    [InlineData("MERGE (n:P)<-[r:R]-(m:P) ON CREATE SET n = {x:1}")]
    public void Parse_Merge_ShouldRoundTripBranches(string source)
    {
        // Arrange / Act
        var first = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        var stage = first.GqlExpression.Stages.ShouldHaveSingleItem().ShouldBeOfType<GqlMergeStage>();
        var second = (GqlQueryStatement)new GqlQueryParser().Parse(stage.ToString());

        // Assert
        first.Diagnostics.ShouldBeEmpty();
        second.Diagnostics.ShouldBeEmpty();
        second.GqlExpression.Stages.ShouldHaveSingleItem().ShouldBeOfType<GqlMergeStage>().ToString().ShouldBe(stage.ToString());
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - MERGE refuses malformed branches and quantified updates (#1735)")]
    [InlineData("MERGE (n), (m)")]
    [InlineData("MERGE (n) ON CREATE SET n.x=1 ON CREATE SET n.y=2")]
    [InlineData("MERGE (n) ON MATCH SET n.x=1 ON MATCH SET n.y=2")]
    [InlineData("MERGE (n) ON CREATE REMOVE n.x")]
    [InlineData("MERGE (n) ON SET n.x=1")]
    [InlineData("MERGE (n)-[:R*1..2]->(m)")]
    [InlineData("MERGE (n)-[:R]->{1,2}(m)")]
    [InlineData("MERGE ((n)-[:R]->(m)){2}")]
    [InlineData("MERGE p=(n)")]
    public void Parse_InvalidMerge_ShouldRefuse(string source)
    {
        // Arrange / Act
        var result = new GqlQueryParser().Parse(source);

        // Assert
        result.Diagnostics.ShouldContain(item => item.Severity == DiagnosticSeverity.Error);
    }
}
