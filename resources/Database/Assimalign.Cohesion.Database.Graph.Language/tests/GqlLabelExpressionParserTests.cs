using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>
/// ISO/IEC 39075 16.8 label expressions in node and edge patterns, and the labeled predicate in
/// <c>WHERE</c> (#1139). <c>!</c> binds tighter than <c>&amp;</c>, <c>&amp;</c> tighter than
/// <c>|</c>, a chain of one operator is one n-ary node, and <c>:A:B</c> is the Cohesion
/// convenience for <c>:A&amp;B</c>. Chain length and nesting have no fixed limit
/// (<see cref="GqlLabelChainParserTests"/>).
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
        // '!' > '&' > '|': flattening a chain never crosses a precedence level.
        { "MATCH (n:A|B&!C) RETURN n", Or(Name("A"), And(Name("B"), Not(Name("C")))), [] },
        { "MATCH (n:!A&B|C) RETURN n", Or(And(Not(Name("A")), Name("B")), Name("C")), [] },
        { "MATCH (n:A|B&C|D) RETURN n", Or(Name("A"), And(Name("B"), Name("C")), Name("D")), [] },
        { "MATCH (n:!A&B) RETURN n", And(Not(Name("A")), Name("B")), [] },
        // A chain of one operator is one n-ary node.
        { "MATCH (n:A|B|C) RETURN n", Or(Name("A"), Name("B"), Name("C")), [] },
        { "MATCH (n:A&B&C) RETURN n", And(Name("A"), Name("B"), Name("C")), ["A", "B", "C"] },
        { "MATCH (n:!(A|B)) RETURN n", Not(Or(Name("A"), Name("B"))), [] },
        { "MATCH (n:!(!A)) RETURN n", Not(Not(Name("A"))), [] },
        // Parentheses group without a node; a conjunction inside them still fills Labels.
        { "MATCH (n:((A&B))) RETURN n", And(Name("A"), Name("B")), ["A", "B"] },
        // A group that opens a chain of its own operator merges into it; a later group stays nested,
        // and a nested conjunction still fills Labels.
        { "MATCH (n:(A|B)|C) RETURN n", Or(Name("A"), Name("B"), Name("C")), [] },
        { "MATCH (n:A|(B|C)) RETURN n", Or(Name("A"), Or(Name("B"), Name("C"))), [] },
        { "MATCH (n:((A&B))&C) RETURN n", And(Name("A"), Name("B"), Name("C")), ["A", "B", "C"] },
        { "MATCH (n:A&(B&C)) RETURN n", And(Name("A"), And(Name("B"), Name("C"))), ["A", "B", "C"] },
        { "MATCH (n:(A|B)&C) RETURN n", And(Or(Name("A"), Name("B")), Name("C")), [] },
        // The Cohesion convenience is the same tree as the conjunction.
        { "MATCH (n:A:B) RETURN n", And(Name("A"), Name("B")), ["A", "B"] },
        { "MATCH (n:A:B:C) RETURN n", And(Name("A"), Name("B"), Name("C")), ["A", "B", "C"] },
        { "MATCH (n:A:A) RETURN n", And(Name("A"), Name("A")), ["A", "A"] },
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
        { "MATCH (a)-[r:T|U|V]->(b) RETURN r", Or(Name("T"), Name("U"), Name("V")), null },
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
        Or(Name("A"), Name("B"), Name("C"), Name("D")).ToString().ShouldBe("A|B|C|D");
        And(Name("A"), Name("B"), Name("C")).ToString().ShouldBe("A&B&C");
        Or(Or(Name("A"), Name("B")), Name("C")).ToString().ShouldBe("(A|B)|C");
        Not(Not(Name("A"))).ToString().ShouldBe("!(!A)");
        // An invalid hand-built node renders a marker instead of throwing.
        new GqlLabelConjunction([]).ToString().ShouldBe("<invalid>");
        new GqlLabelDisjunction(null!).ToString().ShouldBe("<invalid>");
        Or(Name("A"), null!).ToString().ShouldBe("A|<invalid>");
        Name(null!).ToString().ShouldBe("<invalid>");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Label expressions: chains compare by operands in order, and copy their input")]
    public void Equals_LabelChains_ShouldCompareOperandsStructurally()
    {
        // Arrange
        List<GqlLabelExpression> operands = [Name("A"), Name("B")];
        var chain = new GqlLabelDisjunction(operands);

        // Act
        operands.Add(Name("C"));

        // Assert: the record copied its operands, so the caller's list cannot change it.
        chain.Operands.Count.ShouldBe(2);
        chain.ShouldBe(Or(Name("A"), Name("B")));
        chain.GetHashCode().ShouldBe(Or(Name("A"), Name("B")).GetHashCode());
        chain.ShouldNotBe(Or(Name("B"), Name("A")));
        chain.ShouldNotBe(Or(Name("A"), Name("B"), Name("C")));
        ((GqlLabelExpression)chain).ShouldNotBe(And(Name("A"), Name("B")));
        And(Name("A"), Not(Name("B"))).ShouldBe(And(Name("A"), Not(Name("B"))));
        And(Name("A"), Not(Name("B"))).ShouldNotBe(And(Name("A"), Not(Name("b"))));
        new GqlLabelConjunction(null!).ShouldBe(new GqlLabelConjunction(null!));
        new GqlLabelConjunction(null!).ShouldNotBe(new GqlLabelConjunction([]));
    }

    /// <param name="gql">A malformed label expression.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Label expressions: malformed expressions report one GQL0002 in range")]
    [InlineData("MATCH (n:A:B|C) RETURN n")]
    [InlineData("MATCH (n:A|B:C) RETURN n")]
    [InlineData("MATCH (n:!A:B) RETURN n")]
    [InlineData("MATCH (n:A:!B) RETURN n")]
    [InlineData("MATCH (n:%:A) RETURN n")]
    [InlineData("MATCH (n:A:%) RETURN n")]
    [InlineData("MATCH (n:%:%) RETURN n")]
    [InlineData("INSERT (n:%:A)")]
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
        // One AND chain over three Boolean primaries, the last of them parenthesized.
        var and = conjunction.ShouldBeOfType<GqlLogicalExpression>();
        and.Operator.ShouldBe(GqlLogicalOperator.And);
        and.Operands.Count.ShouldBe(3);
        and.Operands[0].ShouldBeOfType<GqlLabeledPredicate>().Variable.ShouldBe("r");
        and.Operands[1].ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("=");
        and.Operands[2].ShouldBeOfType<GqlLabeledPredicate>().LabelExpression.ShouldBe(new GqlLabelWildcard());
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

    private static GqlLabelName Name(string name) => new(name);
    private static GqlLabelNegation Not(GqlLabelExpression operand) => new(operand);
    private static GqlLabelConjunction And(params GqlLabelExpression[] operands) => new(operands);
    private static GqlLabelDisjunction Or(params GqlLabelExpression[] operands) => new(operands);
    private static GqlQueryStatement Parse(string gql) => (GqlQueryStatement)new GqlQueryParser().Parse(gql);
}
