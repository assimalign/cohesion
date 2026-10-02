using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>
/// ISO/IEC 39075 16.8 label expressions in node and edge patterns, and the labeled predicate in
/// <c>WHERE</c> (#1139). <c>!</c> binds tighter than <c>&amp;</c>, <c>&amp;</c> tighter than
/// <c>|</c>, both binary operators associate to the left, and <c>:A:B</c> is the Cohesion
/// convenience for <c>:A&amp;B</c>.
/// </summary>
public sealed class GqlLabelExpressionParserTests
{
    /// <summary>The ISO forms #1101 pinned as COHDBL001 and the precedence cases, with their trees.</summary>
    public static TheoryData<string, GqlLabelExpression, string[]> NodeExpressions => new()
    {
        { "MATCH (n:A|B) RETURN n", Or(Name("A"), Name("B")), [] },
        { "MATCH (n:A&B) RETURN n", And(Name("A"), Name("B")), ["A", "B"] },
        { "MATCH (n:!A) RETURN n", Not(Name("A")), [] },
        { "MATCH (n:%) RETURN n", new GqlLabelWildcard(), [] },
        { "MATCH (n:!%) RETURN n", Not(new GqlLabelWildcard()), [] },
        { "MATCH (n:(A|B)&!C) RETURN n", And(Or(Name("A"), Name("B")), Not(Name("C"))), [] },
        { "MATCH (n IS A|B) RETURN n", Or(Name("A"), Name("B")), [] },
        { "MATCH (n IS A) RETURN n", Name("A"), ["A"] },
        // '!' > '&' > '|', and both binary operators associate to the left.
        { "MATCH (n:A|B&!C) RETURN n", Or(Name("A"), And(Name("B"), Not(Name("C")))), [] },
        { "MATCH (n:!A&B|C) RETURN n", Or(And(Not(Name("A")), Name("B")), Name("C")), [] },
        { "MATCH (n:A|B|C) RETURN n", Or(Or(Name("A"), Name("B")), Name("C")), [] },
        { "MATCH (n:A&B&C) RETURN n", And(And(Name("A"), Name("B")), Name("C")), ["A", "B", "C"] },
        { "MATCH (n:!(A|B)) RETURN n", Not(Or(Name("A"), Name("B"))), [] },
        { "MATCH (n:!(!A)) RETURN n", Not(Not(Name("A"))), [] },
        // Parentheses group without a node; a conjunction inside them still fills Labels.
        { "MATCH (n:((A&B))) RETURN n", And(Name("A"), Name("B")), ["A", "B"] },
        // The Cohesion convenience is the same tree as the conjunction.
        { "MATCH (n:A:B) RETURN n", And(Name("A"), Name("B")), ["A", "B"] },
        { "MATCH (n:A:B:C) RETURN n", And(And(Name("A"), Name("B")), Name("C")), ["A", "B", "C"] },
        // Label names are names: keywords and quoted names are labels, not clauses.
        { "MATCH (n:Order|\"with space\") RETURN n", Or(Name("Order"), Name("with space")), [] },
        { "MATCH (n:A {k: 1}) RETURN n", Name("A"), ["A"] },
    };

    /// <param name="gql">A statement whose first node carries the expression.</param>
    /// <param name="expected">The expected tree.</param>
    /// <param name="labels">The expected <see cref="GqlNodePattern.Labels"/>.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Label expressions: each node form builds its ISO tree and fills Labels only for a conjunction")]
    [MemberData(nameof(NodeExpressions))]
    public void Parse_NodeLabelExpression_ShouldBuildTheIsoTree(string gql, GqlLabelExpression expected, string[] labels)
    {
        // Act
        var statement = Parse(gql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var node = statement.GqlExpression.Matches.ShouldHaveSingleItem().Nodes.ShouldHaveSingleItem();
        node.LabelExpression.ShouldBe(expected);
        node.Labels.ShouldBe(labels);
    }

    /// <summary>The edge forms: a single name fills Type, every other expression leaves it null.</summary>
    public static TheoryData<string, GqlLabelExpression, string?> EdgeExpressions => new()
    {
        { "MATCH (a)-[r:T|U]->(b) RETURN r", Or(Name("T"), Name("U")), null },
        { "MATCH (a)-[r IS T]->(b) RETURN r", Name("T"), "T" },
        { "MATCH (a)-[:%]->(b) RETURN a", new GqlLabelWildcard(), null },
        { "MATCH (a)-[r:!T]->(b) RETURN r", Not(Name("T")), null },
        { "MATCH (a)<-[r:T]-(b) RETURN r", Name("T"), "T" },
        { "MATCH (a)<-[r IS (T|U)&!V {k: 1}]->(b) RETURN r", And(Or(Name("T"), Name("U")), Not(Name("V"))), null },
    };

    /// <param name="gql">A statement whose first relationship carries the expression.</param>
    /// <param name="expected">The expected tree.</param>
    /// <param name="type">The expected <see cref="GqlRelationshipPattern.Type"/>.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Label expressions: each edge form builds its ISO tree and fills Type only for one name")]
    [MemberData(nameof(EdgeExpressions))]
    public void Parse_EdgeLabelExpression_ShouldBuildTheIsoTree(string gql, GqlLabelExpression expected, string? type)
    {
        // Act
        var statement = Parse(gql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var relationship = statement.GqlExpression.Matches.ShouldHaveSingleItem().Relationships.ShouldHaveSingleItem();
        relationship.LabelExpression.ShouldBe(expected);
        relationship.Type.ShouldBe(type);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Label expressions: :A:B is the same tree as :A&B and fills Labels with both")]
    public void Parse_RepeatedColonLabels_ShouldMatchTheConjunction()
    {
        // Act
        var colon = Parse("INSERT (n:A:B)").GqlExpression.Creates.Single().Nodes.Single();
        var conjunction = Parse("INSERT (n:A&B)").GqlExpression.Creates.Single().Nodes.Single();

        // Assert
        colon.LabelExpression.ShouldBe(conjunction.LabelExpression);
        colon.Labels.ShouldBe(["A", "B"]);
        conjunction.Labels.ShouldBe(["A", "B"]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Label expressions: a property map after a label group is not a quantifier")]
    public void Parse_PropertyMapAfterLabelGroup_ShouldNotReportAQuantifier()
    {
        // Act
        var statement = Parse("MATCH (n:(A|B) {k: 1}) RETURN n");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var node = statement.GqlExpression.Matches.Single().Nodes.Single();
        node.LabelExpression.ShouldBe(Or(Name("A"), Name("B")));
        node.Properties["k"].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Label expressions: ToString renders GQL with only the parentheses precedence needs")]
    public void ToString_LabelExpression_ShouldRenderGql()
    {
        // Act / Assert
        And(Or(Name("A"), Name("B")), Not(Name("C"))).ToString().ShouldBe("(A|B)&!C");
        Or(Name("A"), And(Name("B"), Not(Name("C")))).ToString().ShouldBe("A|B&!C");
        Not(Or(Name("A"), new GqlLabelWildcard())).ToString().ShouldBe("!(A|%)");
        And(Name("A"), And(Name("B"), Name("C"))).ToString().ShouldBe("A&(B&C)");
        Or(Name("with space"), Name("Order")).ToString().ShouldBe("\"with space\"|Order");
    }

    /// <param name="gql">A malformed label expression.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Label expressions: malformed expressions report one GQL0002 in range")]
    [InlineData("MATCH (n:A:B|C) RETURN n")]
    [InlineData("MATCH (n:A|B:C) RETURN n")]
    [InlineData("MATCH (n:!A:B) RETURN n")]
    [InlineData("MATCH (n:A:!B) RETURN n")]
    [InlineData("MATCH (n:A|) RETURN n")]
    [InlineData("MATCH (n:&A) RETURN n")]
    [InlineData("MATCH (n:|A) RETURN n")]
    [InlineData("MATCH (n:(A|B) RETURN n")]
    [InlineData("MATCH (n:(A|B))) RETURN n")]
    [InlineData("MATCH (n:A)|B) RETURN n")]
    [InlineData("MATCH (n:A||B) RETURN n")]
    [InlineData("MATCH (n:!!A) RETURN n")]
    [InlineData("MATCH (n:A&) RETURN n")]
    [InlineData("MATCH (n:) RETURN n")]
    [InlineData("MATCH (n IS) RETURN n")]
    [InlineData("MATCH (n IS A:B) RETURN n")]
    [InlineData("MATCH (a)-[:A|:B]->(b) RETURN a")]
    [InlineData("MATCH (a)-[r:T:U]->(b) RETURN r")]
    [InlineData("INSERT (n:A:B|C)")]
    [InlineData("MATCH (n) WHERE n:A:B RETURN n")]
    [InlineData("MATCH (n) WHERE n IS LABELED RETURN n")]
    [InlineData("MATCH (n) WHERE n:A| RETURN n")]
    public void Parse_MalformedLabelExpression_ShouldReportOneSyntaxError(string gql)
    {
        // Act
        var statement = Parse(gql);

        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("GQL0002");
        int start = error.Start.ShouldNotBeNull();
        start.ShouldBeInRange(0, gql.Length);
        error.End.ShouldNotBeNull().ShouldBeInRange(start, gql.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Label expressions: nesting past 128 levels is GQL0005, 128 parses")]
    public void Parse_LabelExpressionNesting_ShouldBeBoundedAt128()
    {
        // Arrange
        static string Parenthesized(int levels) => "MATCH (n:" + new string('(', levels) + "A" + new string(')', levels) + ") RETURN n";
        static string Chain(int names, string separator) => "MATCH (n:" + string.Join(separator, Enumerable.Repeat("A", names)) + ") RETURN n";

        // Act / Assert
        Parse(Parenthesized(128)).Diagnostics.ShouldBeEmpty();
        Parse(Parenthesized(129)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0005");
        Parse(Chain(128, "|")).Diagnostics.ShouldBeEmpty();
        Parse(Chain(129, "|")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0005");
        Parse(Chain(129, "&")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0005");
        Parse(Chain(129, ":")).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0005");
        Parse("MATCH (n) WHERE n:" + new string('(', 129) + "A" + new string(')', 129) + " RETURN n")
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0005");
        // 128 negations inside 128 groups: the groups fit, the tree is 129 levels deep.
        Parse("MATCH (n:" + string.Concat(Enumerable.Repeat("!(", 128)) + "A" + new string(')', 128) + ") RETURN n")
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0005");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Labeled predicate: IS LABELED, the colon form and IS NOT LABELED build one primary")]
    public void Parse_LabeledPredicate_ShouldBuildABooleanPrimary()
    {
        // Act
        var labeled = Parse("MATCH (n) WHERE n IS LABELED A|B RETURN n").GqlExpression.Predicate;
        var colon = Parse("MATCH (n) WHERE n:A|B RETURN n").GqlExpression.Predicate;
        var negated = Parse("MATCH (n) WHERE n is not labeled A RETURN n").GqlExpression.Predicate;
        var conjunction = Parse("MATCH ()-[r]->(n) WHERE r:T AND n.k = 1 AND (n IS LABELED %) RETURN n").GqlExpression.Predicate;

        // Assert
        var first = labeled.ShouldBeOfType<GqlLabeledPredicate>();
        first.Variable.ShouldBe("n");
        first.LabelExpression.ShouldBe(Or(Name("A"), Name("B")));
        first.IsNegated.ShouldBeFalse();
        var second = colon.ShouldBeOfType<GqlLabeledPredicate>();
        second.LabelExpression.ShouldBe(first.LabelExpression);
        second.IsNegated.ShouldBeFalse();
        var third = negated.ShouldBeOfType<GqlLabeledPredicate>();
        third.LabelExpression.ShouldBe(Name("A"));
        third.IsNegated.ShouldBeTrue();
        var and = conjunction.ShouldBeOfType<GqlBinaryExpression>();
        and.Operator.ShouldBe("AND");
        and.Right.ShouldBeOfType<GqlLabeledPredicate>().LabelExpression.ShouldBe(new GqlLabelWildcard());
        var inner = and.Left.ShouldBeOfType<GqlBinaryExpression>();
        inner.Left.ShouldBeOfType<GqlLabeledPredicate>().Variable.ShouldBe("r");
        inner.Right.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("=");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Labeled predicate: its span covers the variable through the last label")]
    public void Parse_LabeledPredicate_ShouldCarryItsSpan()
    {
        // Arrange
        const string gql = "MATCH (n) WHERE n IS NOT LABELED A|B RETURN n";

        // Act
        var location = Parse(gql).GqlExpression.Predicate.ShouldNotBeNull().Location.ShouldNotBeNull();

        // Assert
        location.Start.ShouldBe(gql.IndexOf("n IS", StringComparison.Ordinal));
        location.End.ShouldBe(gql.IndexOf(" RETURN", StringComparison.Ordinal));
    }

    /// <summary>
    /// Label names inside a predicate are never keyword-checked, as in a pattern: Order, Limit
    /// and With are labels here, not ORDER BY, LIMIT or WITH.
    /// </summary>
    /// <param name="gql">A statement whose labels spell keywords.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Labeled predicate: label names are never read as keywords")]
    [InlineData("MATCH (n) WHERE n:A|Order RETURN n")]
    [InlineData("MATCH (n) WHERE n IS LABELED Limit&!(Order|With) RETURN n")]
    [InlineData("MATCH (n) WHERE n IS NOT LABELED Union RETURN n")]
    [InlineData("MATCH (n:A|Order)-[r:Limit|Skip]->(m IS Merge) RETURN n")]
    public void Parse_KeywordLabelNames_ShouldNotReportClauses(string gql)
        => Parse(gql).Diagnostics.ShouldBeEmpty();

    /// <summary>
    /// IS NULL and the truth tests stay with gql-where-expr: only IS [NOT] LABELED passes the
    /// capability scan.
    /// </summary>
    /// <param name="gql">An IS that is not a WHERE labeled predicate.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Labeled predicate: IS NULL and IS in RETURN keep their COHDBL001")]
    [InlineData("MATCH (a) WHERE a.age IS NULL RETURN a")]
    [InlineData("MATCH (a) RETURN a IS LABELED A")]
    public void Parse_IsOutsideLabeledPredicate_ShouldStayUnsupported(string gql)
    {
        // Act
        var diagnostic = Parse(gql).Diagnostics.ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message.ShouldBe("The IS clause is not supported by the GQL surface of this database model.");
        diagnostic.Start.ShouldBe(gql.IndexOf("IS", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Labeled predicate: each counts toward the 128-comparison bound")]
    public void Parse_ManyLabeledPredicates_ShouldBeBounded()
    {
        // Act / Assert
        Parse("MATCH (n) WHERE " + string.Join(" AND ", Enumerable.Repeat("n:A", 128)) + " RETURN n").Diagnostics.ShouldBeEmpty();
        Parse("MATCH (n) WHERE " + string.Join(" AND ", Enumerable.Repeat("n:A", 129)) + " RETURN n")
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0005");
    }

    private static GqlLabelName Name(string name) => new(name);
    private static GqlLabelNegation Not(GqlLabelExpression operand) => new(operand);
    private static GqlLabelConjunction And(GqlLabelExpression left, GqlLabelExpression right) => new(left, right);
    private static GqlLabelDisjunction Or(GqlLabelExpression left, GqlLabelExpression right) => new(left, right);
    private static GqlQueryStatement Parse(string gql) => (GqlQueryStatement)new GqlQueryParser().Parse(gql);
}
