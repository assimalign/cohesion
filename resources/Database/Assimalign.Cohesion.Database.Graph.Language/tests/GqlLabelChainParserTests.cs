using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>
/// Label expressions and predicates have no fixed length or nesting limit (#1139 follow-up, owner
/// decision 2026-10-02: "do what Neo4j does"). A chain of one operator, <c>:A:B:...</c>,
/// <c>A&amp;B&amp;...</c>, <c>A|B|...</c> or <c>p AND q AND ...</c>, is one n-ary node of any length,
/// as Neo4j's <c>Conjunctions</c>, <c>Disjunctions</c> and <c>Ands</c> are, and only the stack bounds
/// genuine nesting: a parse that runs out of it reports <c>GQL0009</c> instead of overflowing.
/// </summary>
public sealed class GqlLabelChainParserTests
{
    private const int chainLength = 10_000;

    /// <summary>Each position a label expression takes, with a 10,000-name chain of each operator.</summary>
    public static TheoryData<string, string, string> Chains => new()
    {
        { "MATCH (n:{0}) RETURN n", "|", "node" },
        { "MATCH (n:{0}) RETURN n", "&", "node" },
        { "MATCH (n:{0}) RETURN n", ":", "node" },
        { "MATCH (n IS {0}) RETURN n", "&", "node" },
        { "MATCH (a)-[r:{0}]->(b) RETURN r", "|", "edge" },
        { "MATCH (a)-[r IS {0}]-(b) RETURN r", "&", "edge" },
        { "MATCH (n) WHERE n:{0} RETURN n", "|", "where" },
        { "MATCH (n) WHERE n IS LABELED {0} RETURN n", "&", "where" },
        { "MATCH (n) WHERE n IS NOT LABELED {0} RETURN n", "|", "where" },
        { "INSERT (n:{0})", ":", "insert" },
        { "INSERT (n:{0})", "&", "insert" },
    };

    /// <param name="template">The statement, with the chain at <c>{0}</c>.</param>
    /// <param name="separator">The chain's operator.</param>
    /// <param name="position">Where the chain sits: node, edge, where or insert.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Label chains: 10,000 names of each operator parse as one n-ary node")]
    [MemberData(nameof(Chains))]
    public void Parse_TenThousandNameChain_ShouldBuildOneNode(string template, string separator, string position)
    {
        // Arrange
        string[] names = Names(chainLength);
        string gql = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, string.Join(separator, names));

        // Act
        var statement = Parse(gql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var query = statement.GqlExpression;
        var expression = position switch
        {
            "node" => query.Matches.Single().Nodes.Single().LabelExpression,
            "edge" => query.Matches.Single().Relationships.Single().LabelExpression,
            "where" => query.Predicate.ShouldBeOfType<GqlLabeledPredicate>().LabelExpression,
            _ => query.Creates.Single().Nodes.Single().LabelExpression,
        };
        var operands = separator == "|"
            ? expression.ShouldBeOfType<GqlLabelDisjunction>().Operands
            : expression.ShouldBeOfType<GqlLabelConjunction>().Operands;
        operands.Select(operand => operand.ShouldBeOfType<GqlLabelName>().Name).ShouldBe(names);
        if (position is "node" or "insert")
        {
            var node = (position == "node" ? query.Matches : query.Creates).Single().Nodes.Single();
            node.Labels.ShouldBe(separator == "|" ? [] : names);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Label chains: 10,000 labeled predicates joined by AND are one chain")]
    public void Parse_TenThousandLabeledPredicates_ShouldBeOneAndChain()
    {
        // Arrange
        string gql = "MATCH (n) WHERE " + string.Join(" AND ", Names(chainLength).Select(name => "n:" + name)) + " RETURN n";

        // Act
        var statement = Parse(gql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var chain = statement.GqlExpression.Predicate.ShouldBeOfType<GqlLogicalExpression>();
        chain.Operator.ShouldBe(GqlLogicalOperator.And);
        chain.Operands.Count.ShouldBe(chainLength);
        chain.Operands[^1].ShouldBeOfType<GqlLabeledPredicate>().LabelExpression.ShouldBe(new GqlLabelName("L9999"));
    }

    /// <param name="text">
    /// A label expression, or the name of a long one: <c>chain|</c>, <c>chain&amp;</c> and
    /// <c>chain:</c> join 10,000 names, <c>alternating</c> is a disjunction of 5,000 two-name
    /// conjunctions, and <c>nested</c> nests 200 negated disjunctions.
    /// </param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Label chains: the rendered text parses back to the same tree")]
    [InlineData("A|B&!C")]
    [InlineData("!A&B|C")]
    [InlineData("(A|B)&!C")]
    [InlineData("A&(B&C)")]
    [InlineData("A|(B|C)")]
    [InlineData("(A|B)|C")]
    [InlineData("!(!(A|%))")]
    [InlineData("!%|A&B&C|!(D&E|F)")]
    [InlineData("\"with space\"|Order")]
    [InlineData("chain|")]
    [InlineData("chain&")]
    [InlineData("chain:")]
    [InlineData("alternating")]
    [InlineData("nested")]
    public void ToString_ParsedExpression_ShouldRoundTrip(string text)
    {
        // Arrange
        text = text switch
        {
            "chain|" => string.Join("|", Names(chainLength)),
            "chain&" => string.Join("&", Names(chainLength)),
            "chain:" => string.Join(":", Names(chainLength)),
            "alternating" => string.Join("|", Names(chainLength).Chunk(2).Select(pair => pair[0] + "&" + pair[1])),
            "nested" => string.Concat(Enumerable.Repeat("!(A|", 200)) + "B" + new string(')', 200),
            _ => text,
        };
        var parsed = NodeExpression("MATCH (n:" + text + ") RETURN n");

        // Act
        string rendered = parsed.ToString();
        var reparsed = NodeExpression("MATCH (n:" + rendered + ") RETURN n");
        var predicate = Parse("MATCH (n) WHERE n IS LABELED " + rendered + " RETURN n");

        // Assert: the same tree, in a pattern and in a labeled predicate, and a stable rendering.
        reparsed.ShouldBe(parsed);
        reparsed.GetHashCode().ShouldBe(parsed.GetHashCode());
        reparsed.ToString().ShouldBe(rendered);
        predicate.Diagnostics.ShouldBeEmpty();
        predicate.GqlExpression.Predicate.ShouldBeOfType<GqlLabeledPredicate>().LabelExpression.ShouldBe(parsed);
    }

    /// <summary>
    /// No fixed depth applies: on a thread with stack to spare, 10,000 nested groups parse in a
    /// pattern, a labeled predicate and a predicate, far past the former 128-level limit.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Label chains: 10,000 nested groups parse on a thread with stack to spare")]
    public void Parse_DeepNesting_ShouldFollowTheStack()
    {
        // Arrange
        const int depth = 10_000;
        string negations = string.Concat(Enumerable.Repeat("!(", depth)) + "A" + new string(')', depth);
        string groups = new string('(', depth) + "a.x = 1" + new string(')', depth);

        // Act
        var (pattern, labeled, predicate) = OnThread(64 * 1024 * 1024, () => (
            Parse("MATCH (n:" + negations + ") RETURN n"),
            Parse("MATCH (n) WHERE n:" + negations + " RETURN n"),
            Parse("MATCH (a) WHERE " + groups + " AND a.y = 2 RETURN a")));

        // Assert
        pattern.Diagnostics.ShouldBeEmpty();
        NegationDepth(pattern.GqlExpression.Matches.Single().Nodes.Single().LabelExpression!).ShouldBe(depth);
        labeled.Diagnostics.ShouldBeEmpty();
        NegationDepth(labeled.GqlExpression.Predicate.ShouldBeOfType<GqlLabeledPredicate>().LabelExpression).ShouldBe(depth);
        predicate.Diagnostics.ShouldBeEmpty();
        // The group that opens the chain holds one comparison, so it is that comparison.
        predicate.GqlExpression.Predicate.ShouldBeOfType<GqlLogicalExpression>().Operands.Count.ShouldBe(2);
    }

    /// <summary>
    /// Nesting deeper than the parsing thread's stack is one GQL0009 at the group that could not be
    /// entered, never a process crash, and the parser instance serves the next statement.
    /// </summary>
    /// <param name="template">The statement, with the nested text at <c>{0}</c>.</param>
    /// <param name="open">The text that opens one level.</param>
    /// <param name="close">The text that closes one level.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Label chains: nesting past a small thread's stack is GQL0009, not a crash")]
    [InlineData("MATCH (n:{0}) RETURN n", "(", ")")]
    [InlineData("MATCH (n:{0}) RETURN n", "!(", ")")]
    [InlineData("MATCH (a)-[r:{0}]->(b) RETURN r", "(T|", ")")]
    [InlineData("MATCH (n) WHERE n:{0} RETURN n", "(A&", ")")]
    [InlineData("MATCH (n) WHERE {0} RETURN n", "(n.x = 1 AND ", ")")]
    [InlineData("INSERT (n:{0})", "(", ")")]
    public void Parse_NestingPastTheStack_ShouldReportGql0009(string template, string open, string close)
    {
        // Arrange
        const int depth = 100_000;
        string nested = string.Concat(Enumerable.Repeat(open, depth)) + (template.Contains("WHERE {0}", StringComparison.Ordinal) ? "n.x = 1" : "A") +
            string.Concat(Enumerable.Repeat(close, depth));
        string gql = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, nested);
        var parser = new GqlQueryParser();

        // Act
        var (deep, next) = OnThread(512 * 1024, () => (
            (GqlQueryStatement)parser.Parse(gql),
            (GqlQueryStatement)parser.Parse("MATCH (n:A|B) RETURN n")));

        // Assert
        var error = deep.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("GQL0009");
        error.Message.ShouldNotBeNull().ShouldContain("stack", Case.Sensitive);
        int start = error.Start.ShouldNotBeNull();
        start.ShouldBeInRange(0, gql.Length - 1);
        gql[start].ShouldBe('(');
        error.End.ShouldBe(start + 1);
        next.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Label chains: rendering, equality and hashing hold for a tree of any depth")]
    public void Walks_VeryDeepTree_ShouldNotOverflow()
    {
        // Arrange: far deeper than any default thread's stack could recurse through.
        const int depth = 200_000;
        static GqlLabelExpression Build(string leaf)
        {
            GqlLabelExpression expression = new GqlLabelName(leaf);
            for (int i = 0; i < depth; i++)
            {
                expression = i % 2 == 0 ? new GqlLabelNegation(expression) : new GqlLabelConjunction([expression, new GqlLabelWildcard()]);
            }
            return expression;
        }
        var first = Build("A");
        var second = Build("A");
        var different = Build("B");

        // Act
        string text = first.ToString();

        // Assert: the outermost node is a conjunction whose first operand is a negated group.
        text.Length.ShouldBeGreaterThan(depth);
        text.ShouldStartWith("!(!(");
        text.ShouldEndWith(")&%");
        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
        first.ShouldNotBe(different);
    }

    private static string[] Names(int count) => Enumerable.Range(0, count).Select(i => "L" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static GqlLabelExpression NodeExpression(string gql)
    {
        var statement = Parse(gql);
        statement.Diagnostics.ShouldBeEmpty();
        return statement.GqlExpression.Matches.Single().Nodes.Single().LabelExpression.ShouldNotBeNull();
    }

    private static int NegationDepth(GqlLabelExpression expression)
    {
        int depth = 0;
        while (expression is GqlLabelNegation negation)
        {
            depth++;
            expression = negation.Operand;
        }
        expression.ShouldBe(new GqlLabelName("A"));
        return depth;
    }

    private static T OnThread<T>(int maxStackSize, Func<T> action)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        }, maxStackSize);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    private static GqlQueryStatement Parse(string gql) => (GqlQueryStatement)new GqlQueryParser().Parse(gql);
}
