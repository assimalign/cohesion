using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlSetQueryParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - Set conjunctions: multiplicities render canonically")]
    [InlineData("EXCEPT", GqlCompositeOperator.Except, "EXCEPT")]
    [InlineData("EXCEPT DISTINCT", GqlCompositeOperator.Except, "EXCEPT")]
    [InlineData("EXCEPT ALL", GqlCompositeOperator.ExceptAll, "EXCEPT ALL")]
    [InlineData("INTERSECT", GqlCompositeOperator.Intersect, "INTERSECT")]
    [InlineData("INTERSECT DISTINCT", GqlCompositeOperator.Intersect, "INTERSECT")]
    [InlineData("INTERSECT ALL", GqlCompositeOperator.IntersectAll, "INTERSECT ALL")]
    [InlineData("OTHERWISE", GqlCompositeOperator.Otherwise, "OTHERWISE")]
    public void Parse_SetQuantifier_ShouldRoundTrip(string text, GqlCompositeOperator op, string canonical)
    {
        // Arrange / Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN 1 AS x " + text + " (RETURN 2 AS x)");
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        parsed.GqlExpression.Composite.ShouldNotBeNull().Operator.ShouldBe(op);
        string rendered = parsed.GqlExpression.ToString();
        rendered.ShouldContain(") " + canonical + " (", Case.Sensitive);
        var roundtrip = (GqlQueryStatement)new GqlQueryParser().Parse(rendered);
        roundtrip.Diagnostics.ShouldBeEmpty();
        roundtrip.GqlExpression.Composite!.Operator.ShouldBe(op);
    }

    [Fact(DisplayName = "Cohesion Test [GQL] - Composite conjunctions: one precedence level associates left before NEXT")]
    public void Parse_MixedConjunctions_ShouldRetainExplicitTree()
    {
        // Arrange / Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN 1 AS x OTHERWISE RETURN 2 AS x UNION ALL RETURN 3 AS x INTERSECT RETURN 3 AS x NEXT RETURN x");
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        var root = parsed.GqlExpression.Stages[0].ShouldBeOfType<GqlCompositeStage>().Query;
        root.Operator.ShouldBe(GqlCompositeOperator.Intersect);
        root.Left.Composite!.Operator.ShouldBe(GqlCompositeOperator.UnionAll);
        root.Left.Composite.Left.Composite!.Operator.ShouldBe(GqlCompositeOperator.Otherwise);
        parsed.GqlExpression.Stages[1].ShouldBeOfType<GqlNextStage>();
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Composite conjunctions: missing operands and invalid fallback quantifiers have located syntax diagnostics")]
    [InlineData("EXCEPT")]
    [InlineData("EXCEPT ALL")]
    [InlineData("INTERSECT DISTINCT")]
    [InlineData("OTHERWISE")]
    [InlineData("OTHERWISE ALL RETURN 2 AS x")]
    [InlineData("OTHERWISE DISTINCT RETURN 2 AS x")]
    public void Parse_MissingOperand_ShouldReportSyntaxCode(string tail)
    {
        // Arrange / Act
        var diagnostic = new GqlQueryParser().Parse("RETURN 1 AS x " + tail).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CG00015");
        diagnostic.Start.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Composite groups: capability scanning resets context for every conjunction")]
    [InlineData("EXCEPT")]
    [InlineData("EXCEPT ALL")]
    [InlineData("INTERSECT DISTINCT")]
    [InlineData("OTHERWISE")]
    public void Parse_GroupedUnsupportedOperand_ShouldReportCapability(string op)
    {
        // Arrange / Act
        var diagnostic = new GqlQueryParser().Parse("RETURN 1 AS x " + op + " (CALL missing())").Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message!.ShouldContain("CALL", Case.Sensitive);
    }
}
