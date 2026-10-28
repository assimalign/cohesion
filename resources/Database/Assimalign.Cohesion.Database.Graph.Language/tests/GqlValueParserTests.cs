using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlValueParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - Values: conditional and composite expressions parse")]
    [InlineData("CASE a.age WHEN 17, 42 THEN 1 END")]
    [InlineData("CASE a.age WHEN > 30, IS NULL THEN 1 WHEN IS NOT NULL THEN 2 END")]
    [InlineData("[(a.age), a:Person|Robot]")]
    [InlineData("[a = 1, a:A|B]")]
    [InlineData("CASE WHEN a.age = 42 THEN 1 END")]
    [InlineData("CASE a.age WHEN 42 THEN CASE WHEN TRUE THEN 2 END ELSE 3 END")]
    [InlineData("COALESCE(a.absent, a.name)")]
    [InlineData("NULLIF(a.age, 42)")]
    [InlineData("[1, a.name, [2]]")]
    [InlineData("[(1), (a:Person|Robot)]")]
    [InlineData("{name: a.name, n: 1}")]
    [InlineData("{total: count(a)}")]
    [InlineData("[10, 20, 30][-1]")]
    [InlineData("{k: 1}['k']")]
    [InlineData("{k: {j: 1}}.k.j")]
    [InlineData("$m.k")]
    [InlineData("$l[0]")]
    [InlineData("a.age IN $ages")]
    [InlineData("a.age IN ($ages)")]
    [InlineData("a.age IN ([17,42])")]
    public void Parse_ValueExpression_ShouldSucceed(string value)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse("MATCH (a) RETURN " + value);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Values: truncated lists report syntax errors at EOF")]
    [InlineData("RETURN [")]
    [InlineData("RETURN [[")]
    [InlineData("MATCH (a) WHERE a.x IN [")]
    [InlineData("RETURN {k: [")]
    public void Parse_TruncatedList_ShouldReportSyntaxDiagnostic(string source)
    {
        // Arrange / Act / Assert
        foreach (string suffix in new[] { "", " -- trailing comment", " /* trailing comment */" })
        {
            var error = new GqlQueryParser().Parse(source + suffix).Diagnostics.ShouldHaveSingleItem();
            error.Code.ShouldBe("CG00002");
            error.Start.ShouldBe((source + suffix).Length);
        }
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Values: unsupported map values retain capability diagnostics")]
    [InlineData("{n: reverse(1)}")]
    [InlineData("{e: EXISTS {(a)->(b)}}")]
    public void Parse_UnsupportedMapValue_ShouldRetainCapabilityDiagnostic(string value)
    {
        // Arrange / Act
        var errors = new GqlQueryParser().Parse("MATCH (a) RETURN " + value).Diagnostics;
        // Assert
        errors.ShouldContain(error => error.Code == "CK00001");
        errors.ShouldNotContain(error => error.Code == "CG00002");
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Values: malformed CASE identifies the offending token")]
    [InlineData("RETURN CASE END", "END")]
    [InlineData("RETURN CASE WHEN TRUE ELSE 1 END", "ELSE")]
    [InlineData("RETURN CASE 1 WHEN 1 2 END", "2")]
    public void Parse_MalformedCase_ShouldLocateToken(string source, string token)
    {
        // Act
        var error = new GqlQueryParser().Parse(source).Diagnostics.First();
        // Assert
        error.Code.ShouldBe("CG00002");
        source[error.Start!.Value..error.End!.Value].ShouldBe(token);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Values: excluded collection syntax is a precise capability diagnostic")]
    [InlineData("[(a)->(b) | b]", "PATTERN COMPREHENSION")]
    [InlineData("[p=(a)->(b) | p]", "PATTERN COMPREHENSION")]
    [InlineData("a.b.c", "NESTED PROPERTY ACCESS")]
    [InlineData("[x IN [a] WHERE x:A | x.b.c]", "NESTED PROPERTY ACCESS")]
    public void Parse_ExcludedValueForm_ShouldNameConstruct(string expression, string construct)
    {
        // Act
        var diagnostics = new GqlQueryParser().Parse("MATCH (a) RETURN " + expression).Diagnostics;
        // Assert
        diagnostics.ShouldContain(item => item.Code == "CK00001" && item.Message!.Contains(construct));
    }
}
