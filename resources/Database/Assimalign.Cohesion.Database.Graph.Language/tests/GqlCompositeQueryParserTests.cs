using System.Linq;
using System.Text;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlCompositeQueryParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - NEXT: standalone ordering and paging retain their capability diagnostic")]
    [InlineData("ORDER BY x")]
    [InlineData("SKIP 1")]
    [InlineData("OFFSET 1")]
    [InlineData("LIMIT 1")]
    public void Parse_NextStandalonePage_ShouldNameCapability(string tail)
    {
        // Arrange
        string source = "RETURN 1 AS x NEXT " + tail;
        // Act
        var diagnostic = new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message!.ShouldContain("STANDALONE ORDER BY AND PAGE STATEMENT", Case.Sensitive);
        diagnostic.Start.ShouldBe(source.IndexOf(tail, System.StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - UNION: preserves left association and grouped operands")]
    [InlineData("RETURN 1 AS x UNION RETURN 2 AS x UNION RETURN 3 AS x", false)]
    [InlineData("RETURN 1 AS x UNION (RETURN 2 AS x UNION RETURN 3 AS x)", true)]
    public void Parse_CompositeQuery_ShouldPreserveGrouping(string source, bool rightGroup)
    {
        // Arrange / Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        var root = parsed.GqlExpression.Composite.ShouldNotBeNull();
        root.Operator.ShouldBe(GqlCompositeOperator.Union);
        (root.Right.Composite is not null).ShouldBe(rightGroup);
        (root.Left.Composite is not null).ShouldBe(!rightGroup);
        var rendered = root.ToString();
        var roundtrip = (GqlQueryStatement)new GqlQueryParser().Parse(rendered);
        roundtrip.Diagnostics.ShouldBeEmpty();
        roundtrip.GqlExpression.Composite!.ToString().ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - UNION: supports linear and correlated operands and per-operand paging")]
    [InlineData("(RETURN 1 NEXT RETURN 2) UNION RETURN 3")]
    [InlineData("(RETURN 1 AS x NEXT RETURN x+1 AS x) UNION ALL (RETURN 2 AS x NEXT RETURN x+2 AS x)")]
    [InlineData("EXPLAIN (RETURN 1 AS x UNION ALL RETURN 2 AS x)")]
    [InlineData("FOR v IN [1,2] RETURN v ORDER BY v DESC SKIP 1 LIMIT 1 UNION ALL RETURN 3 AS v")]
    [InlineData("MATCH (a) WHERE EXISTS { (a)->(b) } RETURN [(a)->(b) | b.k] AS xs UNION RETURN [] AS xs")]
    [InlineData("LET except=1,intersect=2,otherwise=3 RETURN except+intersect+otherwise UNION RETURN 6 AS x")]
    public void Parse_ReadOnlyComposite_ShouldParseAndRender(string source)
    {
        // Arrange / Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        var rendered = parsed.GqlExpression.Composite.ShouldNotBeNull().ToString();
        new GqlQueryParser().Parse(rendered).Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - UNION: missing operands have a located syntax code")]
    [InlineData("RETURN 1 UNION")]
    [InlineData("RETURN 1 UNION ALL")]
    [InlineData("RETURN 1 UNION ()")]
    public void Parse_MissingOperand_ShouldRefuseWithCode(string source)
    {
        // Arrange / Act
        var diagnostic = new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CG00015");
        diagnostic.Start.ShouldNotBeNull();
        diagnostic.End!.Value.ShouldBeLessThanOrEqualTo(source.Length);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Composite conjunctions parse and roundtrip")]
    [InlineData("EXCEPT")]
    [InlineData("INTERSECT")]
    [InlineData("OTHERWISE")]
    public void Parse_CompositeOperator_ShouldRoundTrip(string operation)
    {
        // Arrange / Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN 1 AS x " + operation + " RETURN 2 AS x");
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        string rendered = parsed.GqlExpression.Composite.ShouldNotBeNull().ToString();
        rendered.ShouldContain(operation, Case.Sensitive);
        new GqlQueryParser().Parse(rendered).Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - UNION: unsupported modifiers and global page suffixes remain named capabilities")]
    [InlineData("(RETURN 1 AS x UNION RETURN 2 AS x) ORDER BY x", "COMPOSITE ORDER BY AND PAGE STATEMENT")]
    [InlineData("(RETURN 1 AS x UNION RETURN 2 AS x NEXT RETURN x) ORDER BY x", "COMPOSITE ORDER BY AND PAGE STATEMENT")]
    public void Parse_ExcludedCompositeForm_ShouldNameCapability(string source, string form)
    {
        // Arrange / Act
        var diagnostic = new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message!.ShouldContain(form, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [GQL] - UNION: flat chains render without recursive stack growth")]
    public void Parse_LongUnionChain_ShouldRenderIteratively()
    {
        // Arrange
        var source = new StringBuilder("RETURN 0 AS x");
        for (int i = 1; i < 1000; i++) { source.Append(" UNION ALL RETURN ").Append(i).Append(" AS x"); }
        // Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(source.ToString());
        var rendered = parsed.GqlExpression.Composite.ShouldNotBeNull().ToString();
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        rendered.Split("UNION ALL").Length.ShouldBe(1000);
    }
}
