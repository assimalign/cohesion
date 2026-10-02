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
    [InlineData("MATCH (a)<-[r]->(b) RETURN a")]
    [InlineData("MATCH (a)->(b)<-(c)-(d)<->(e) RETURN a, e")]
    [InlineData("MATCH (n:A|B)-[r:T|U]->(m IS !C) WHERE n IS LABELED A AND r:T RETURN n")]
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
    // ISO comments after a pattern keep their meaning when whitespace separates them, and a ')'
    // that closes a predicate is not a pattern element (#1139).
    [InlineData("MATCH (a)-[r]->(b) -- note\nRETURN a")]
    [InlineData("MATCH (a) WHERE (a.age > 1)-- note\nRETURN a")]
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
    [InlineData("MATCH (a)<--(b) RETURN a", "GQL0002")]
    [InlineData("MATCH (a)<-->(b) RETURN a", "GQL0002")]
    [InlineData("MATCH (a)-[r]-->(b) RETURN a", "GQL0008")]
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
        path.Nodes[0].LabelExpression.ShouldBe(new GqlLabelName("Person"));
        path.Relationships[0].LabelExpression.ShouldBe(new GqlLabelName("KNOWS"));
        path.Nodes[1].LabelExpression.ShouldBeNull();
        path.Relationships[1].LabelExpression.ShouldBeNull();
        query.Projections.ShouldBe([new GqlProjection("a"), new GqlProjection("r"), new GqlProjection("b", "name", "friend")]);
    }

    [Fact]
    public void PredicateAst_PreservesConjunctionAndNormalizedComparison()
    {
        var query = Parse("MATCH (a) WHERE a.age >= 18 AND a.name <> 'Bob' RETURN a").GqlExpression;
        var conjunction = query.Predicate.ShouldBeOfType<GqlLogicalExpression>();
        conjunction.Operator.ShouldBe(GqlLogicalOperator.And);
        conjunction.Operands.Count.ShouldBe(2);
        var age = conjunction.Operands[0].ShouldBeOfType<GqlBinaryExpression>();
        age.Operator.ShouldBe(">=");
        age.Left.ShouldBeOfType<GqlPropertyExpression>().Property.ShouldBe("age");
        age.Right.ShouldBeOfType<GqlLiteralExpression>().Value.ShouldBe(18L);
        conjunction.Operands[1].ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("!=");
    }

    /// <summary>
    /// An AND chain is one n-ary node, as Neo4j's Ands: a group that opens the chain merges into it,
    /// a group in a later position stays nested, and a single comparison is not wrapped.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Predicates: an AND chain is one n-ary node")]
    public void Parse_AndChain_ShouldBuildOneLogicalNode()
    {
        // Act
        var chain = Parse("MATCH (a) WHERE a.x = 1 AND a.x = 2 AND a.x = 3 AND a.x = 4 RETURN a").GqlExpression.Predicate;
        var opening = Parse("MATCH (a) WHERE (a.x = 1 AND a.x = 2) AND a.x = 3 RETURN a").GqlExpression.Predicate;
        var later = Parse("MATCH (a) WHERE a.x = 1 AND (a.x = 2 AND a.x = 3) RETURN a").GqlExpression.Predicate;
        var single = Parse("MATCH (a) WHERE ((a.x = 1)) RETURN a").GqlExpression.Predicate;

        // Assert
        chain.ShouldBeOfType<GqlLogicalExpression>().Operands.Count.ShouldBe(4);
        var merged = opening.ShouldBeOfType<GqlLogicalExpression>();
        merged.Operands.Count.ShouldBe(3);
        merged.Operands.ShouldAllBe(operand => operand is GqlBinaryExpression);
        var nested = later.ShouldBeOfType<GqlLogicalExpression>();
        nested.Operands.Count.ShouldBe(2);
        nested.Operands[1].ShouldBeOfType<GqlLogicalExpression>().Operands.Count.ShouldBe(2);
        single.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("=");
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

    /// <summary>
    /// A path pattern keeps its 64-relationship bound (GQL0005). Predicates have no count or depth
    /// limit (#1139 follow-up): a 10,000-comparison AND chain is one node, and parentheses nest
    /// as deep as the stack allows (GqlLabelChainParserTests covers the stack).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Bounds: only the 64-relationship path is bounded")]
    public void Parse_LongPredicateAndLongPath_ShouldBoundOnlyThePath()
    {
        Parse("MATCH (a)" + string.Concat(Enumerable.Repeat("-[]->()", 65)) + " RETURN a")
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0005");
        Parse("MATCH (a)" + string.Concat(Enumerable.Repeat("-[]->()", 64)) + " RETURN a").Diagnostics.ShouldBeEmpty();
        var chain = Parse("MATCH (a) WHERE " + string.Join(" AND ", Enumerable.Repeat("a.x = 1", 10_000)) + " RETURN a");
        chain.Diagnostics.ShouldBeEmpty();
        chain.GqlExpression.Predicate.ShouldBeOfType<GqlLogicalExpression>().Operands.Count.ShouldBe(10_000);
        var deep = Parse("MATCH (a) WHERE " + new string('(', 300) + "a.x = 1" + new string(')', 300) + " RETURN a");
        deep.Diagnostics.ShouldBeEmpty();
        deep.GqlExpression.Predicate.ShouldBeOfType<GqlBinaryExpression>();
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
