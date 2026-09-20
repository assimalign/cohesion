using System;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Language;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>Executable-subset corpus mapped to ISO/IEC 39075:2024 in docs/DESIGN.md.</summary>
public class GqlQueryParserTests
{
    [Theory]
    [InlineData("MATCH (a:Person) RETURN a")]
    [InlineData("MATCH ()-[r:KNOWS]->() RETURN r")]
    [InlineData("MATCH (a)<-[r:KNOWS]-(b) RETURN a, r, b")]
    [InlineData("MATCH (a)-[r]-(b) RETURN a, r, b")]
    [InlineData("MATCH (a)-[r]->(b)-[s]->(c) RETURN a, r, b, s, c")]
    [InlineData("MATCH (a:Person {name: 'Alice'}), (b:Person {name: 'Bob'}) INSERT (a)-[r:KNOWS]->(b) RETURN r")]
    [InlineData("MATCH (a) WHERE a.age >= 18 AND a.name <> 'Bob' RETURN a.name AS name")]
    [InlineData("MATCH (a) WHERE (a.active = TRUE AND (a.score < 10.5)) RETURN a")]
    [InlineData("MATCH (a) WHERE a.left = a.right RETURN a")]
    [InlineData("MATCH (a) WHERE NULL = a.absent RETURN a")]
    [InlineData("INSERT (a:Person {name: 'Alice', age: -42, active: TRUE, optional: NULL})")]
    [InlineData("INSERT (a:Person), (b:Person), (a)-[r:KNOWS {weight: .5}]->(b) RETURN a, r, b")]
    [InlineData("CREATE (a:Person {name: 'Alice'}) RETURN a")]
    [InlineData("MATCH (a:Person) DELETE a")]
    [InlineData("MATCH (a:Person) DETACH DELETE a")]
    [InlineData("MATCH (a)-[r]->(b) DELETE r, a")]
    [InlineData("match (a:Person {\"with space\": 'it''s fine'}) return a.\"with space\";")]
    [InlineData("/* outer /* inner */ */ MATCH (a) -- comment\n RETURN a")]
    [InlineData("CREATE (a:Person {limit: 2, count: 3, value: 'OPTIONAL MATCH'}) RETURN a.limit")]
    public void SupportedCorpus_ParsesWithoutDiagnostics(string source)
    {
        var statement = Parse(source);
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.Text.ShouldBe(source);
    }

    [Theory]
    [InlineData("", "GQL0001")]
    [InlineData("MATCH (a RETURN a", "GQL0002")]
    [InlineData("MATCH a RETURN a", "GQL0002")]
    [InlineData("MATCH (a)-[r->(b) RETURN a", "GQL0002")]
    [InlineData("MATCH (a)<-[r]->(b) RETURN a", "GQL0002")]
    [InlineData("MATCH (a)-[r]-> RETURN a", "GQL0002")]
    [InlineData("MATCH (a)", "GQL0002")]
    [InlineData("MATCH (a) RETURN", "GQL0002")]
    [InlineData("MATCH (a) WHERE a.age RETURN a", "GQL0002")]
    [InlineData("MATCH (a) WHERE a.age = RETURN a", "GQL0002")]
    [InlineData("MATCH (a) WHERE a.age = 2 = 3 RETURN a", "GQL0002")]
    [InlineData("MATCH (a) RETURN a; MATCH (b) RETURN b", "GQL0002")]
    [InlineData("CREATE (:Person {age: 1, age: 2})", "GQL0006")]
    [InlineData("CREATE (:Person {age: 9223372036854775808})", "GQL0004")]
    [InlineData("CREATE (:Person {age: 1e999})", "GQL0004")]
    [InlineData("CREATE (:Person {age: 1e})", "GQL0004")]
    [InlineData("CREATE (:Person {name: 'unterminated})", "GQL0003")]
    [InlineData("MATCH (\"unterminated) RETURN a", "GQL0003")]
    [InlineData("/* unterminated", "GQL0003")]
    [InlineData("MATCH (\"\") RETURN a", "GQL0002")]
    [InlineData("MATCH (other.Person) RETURN a", "GQL0002")]
    public void MalformedCorpus_ReturnsStableDiagnostic(string source, string code)
    {
        var statement = Parse(source);
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        statement.Diagnostics.ShouldAllBe(diagnostic => diagnostic.Start >= 0 && diagnostic.End <= source.Length);
    }

    [Theory]
    [InlineData("OPTIONAL MATCH (a) RETURN a")]
    [InlineData("MANDATORY MATCH (a) RETURN a")]
    [InlineData("MATCH (a) RETURN a ORDER BY a.age")]
    [InlineData("MATCH (a) RETURN a LIMIT 5")]
    [InlineData("MATCH (a) RETURN a OFFSET 5")]
    [InlineData("MATCH (a) RETURN a SKIP 5")]
    [InlineData("MATCH (a) WITH a RETURN a")]
    [InlineData("MATCH (a) SET a.age = 5")]
    [InlineData("MATCH (a) REMOVE a.age")]
    [InlineData("MERGE (a:Person)")]
    [InlineData("MATCH (a) RETURN a UNION MATCH (b) RETURN b")]
    [InlineData("MATCH (a) RETURN DISTINCT a")]
    [InlineData("MATCH (a) WHERE a.age = 1 OR a.age = 2 RETURN a")]
    [InlineData("MATCH (a) WHERE a.age IS NULL RETURN a")]
    [InlineData("MATCH (a) RETURN count(a)")]
    [InlineData("MATCH (a) WHERE abs(a.age) = 1 RETURN a")]
    [InlineData("MATCH (a) RETURN *")]
    [InlineData("MATCH (a)-[r*1..3]->(b) RETURN a")]
    [InlineData("MATCH (a)-[r]->{1,3}(b) RETURN a")]
    [InlineData("MATCH (a) WHERE a.age = $age RETURN a")]
    [InlineData("INSERT (:Person {tags: [1, 2]})")]
    [InlineData("MATCH (a) DELETE a RETURN a")]
    [InlineData("CALL foo()")]
    [InlineData("UNWIND [1, 2] AS a RETURN a")]
    [InlineData("USE other")]
    [InlineData("CREATE DATABASE other")]
    [InlineData("CREATE GRAPH other")]
    [InlineData("DROP GRAPH other")]
    [InlineData("ALTER SCHEMA other")]
    [InlineData("SHOW DATABASES")]
    [InlineData("SESSION SET GRAPH other")]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("COMMIT")]
    public void OutsideProfileCorpus_ReportsUnsupportedClause(string source)
    {
        Parse(source).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHDBL001");
    }

    [Fact]
    public void MatchAst_PreservesDirectionsLabelsBindingsAndLiteralProperties()
    {
        var query = Parse("MATCH (a:Person {name: 'Alice'})<-[r:KNOWS {weight: 2}]-(b)-[s]-(c) RETURN a, r, b.name AS friend").GqlExpression;
        query.Matches.Count.ShouldBe(1);
        var path = query.Matches[0];
        path.Nodes.Select(node => node.Variable).ShouldBe(["a", "b", "c"]);
        path.Nodes[0].Labels.ShouldBe(["Person"]);
        path.Nodes[0].Properties["name"].ShouldBe("Alice");
        path.Relationships[0].Variable.ShouldBe("r");
        path.Relationships[0].Type.ShouldBe("KNOWS");
        path.Relationships[0].Direction.ShouldBe(GqlPatternDirection.Incoming);
        path.Relationships[0].Properties["weight"].ShouldBe(2L);
        path.Relationships[1].Direction.ShouldBe(GqlPatternDirection.Undirected);
        query.Projections.ShouldBe([new GqlProjection("a"), new GqlProjection("r"), new GqlProjection("b", "name", "friend")]);
    }

    [Fact]
    public void PredicateAst_PreservesConjunctionAndNormalizedComparison()
    {
        var query = Parse("MATCH (a) WHERE a.age >= 18 AND a.name <> 'Bob' RETURN a").GqlExpression;
        var conjunction = query.Predicate.ShouldBeOfType<GqlBinaryExpression>();
        conjunction.Operator.ShouldBe("AND");
        var age = conjunction.Left.ShouldBeOfType<GqlBinaryExpression>();
        age.Operator.ShouldBe(">=");
        age.Left.ShouldBeOfType<GqlPropertyExpression>().Property.ShouldBe("age");
        age.Right.ShouldBeOfType<GqlLiteralExpression>().Value.ShouldBe(18L);
        conjunction.Right.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("!=");
    }

    [Fact]
    public void MutationAst_PreservesMatchingCreationAndDetachDeletion()
    {
        var create = Parse("MATCH (a:Person), (b:Person) WHERE a.name = 'Alice' CREATE (a)-[r:KNOWS]->(b) RETURN r").GqlExpression;
        create.Matches.Count.ShouldBe(2);
        create.Creates.Single().Relationships.Single().Type.ShouldBe("KNOWS");
        create.DeleteVariables.ShouldBeEmpty();
        create.DetachDelete.ShouldBeFalse();
        var delete = Parse("MATCH (a:Person) DETACH DELETE a").GqlExpression;
        delete.DeleteVariables.ShouldBe(["a"]);
        delete.DetachDelete.ShouldBeTrue();
    }

    [Fact]
    public void SignedIntegerBoundariesAndEscapedString_PreserveValues()
    {
        var properties = Parse("INSERT (:P {min: -9223372036854775808, max: 9223372036854775807, text: 'a''b', n: -2.5e2})")
            .GqlExpression.Creates.Single().Nodes.Single().Properties;
        properties["min"].ShouldBe(long.MinValue);
        properties["max"].ShouldBe(long.MaxValue);
        properties["text"].ShouldBe("a'b");
        properties["n"].ShouldBe(-250d);
    }

    [Fact]
    public void DiagnosticSpan_UsesAbsoluteUtf16OffsetAndOneBasedLine()
    {
        const string source = "MATCH (a)\nRETURN a LIMIT 2";
        var diagnostic = Parse(source).Diagnostics.Single();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Start.ShouldBe(source.IndexOf("LIMIT", StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + 5);
        diagnostic.Line.ShouldBe(2);
        diagnostic.Location.ShouldBe(DiagnosticLocation.Absolute);
    }

    [Fact]
    public void DeepPredicateAndLongPath_ReturnBoundDiagnostics()
    {
        Parse("MATCH (a) WHERE " + new string('(', 129) + "a.x = 1" + new string(')', 129) + " RETURN a")
            .Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "GQL0005");
        Parse("MATCH (a)" + string.Concat(Enumerable.Repeat("-[]->()", 65)) + " RETURN a")
            .Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "GQL0005");
        Parse("MATCH (a) WHERE " + string.Join(" AND ", Enumerable.Repeat("a.x = 1", 129)) + " RETURN a")
            .Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "GQL0005");
        Parse("MATCH (a) WHERE " + string.Join(" AND ", Enumerable.Repeat("a.x = 1", 128)) + " RETURN a")
            .Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParserInstance_ResetsDiagnosticsAndSerializesConcurrentCalls()
    {
        var parser = new GqlQueryParser();
        parser.Parse("MATCH (").Diagnostics.ShouldNotBeEmpty();
        parser.Parse("MATCH (a) RETURN a").Diagnostics.ShouldBeEmpty();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
        {
            string source = $"INSERT (a:P {{number: {index}}}) RETURN a";
            var parsed = (GqlQueryStatement)parser.Parse(source);
            parsed.Diagnostics.ShouldBeEmpty();
            parsed.GqlExpression.Text.ShouldBe(source);
            parsed.GqlExpression.Creates[0].Nodes[0].Properties["number"].ShouldBe((long)index);
        })));
    }

    private static GqlQueryStatement Parse(string source) => (GqlQueryStatement)new GqlQueryParser().Parse(source);
}
