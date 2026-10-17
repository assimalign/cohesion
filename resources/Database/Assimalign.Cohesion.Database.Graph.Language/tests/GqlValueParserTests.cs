using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlValueParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - Values: conditional and composite expressions parse")]
    [InlineData("CASE WHEN a.age = 42 THEN 1 END")]
    [InlineData("CASE a.age WHEN 42 THEN CASE WHEN TRUE THEN 2 END ELSE 3 END")]
    [InlineData("COALESCE(a.absent, a.name)")]
    [InlineData("NULLIF(a.age, 42)")]
    [InlineData("[1, a.name, [2]]")]
    [InlineData("[(1), (a:Person|Robot)]")]
    [InlineData("{name: a.name, n: 1}")]
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

    [Theory(DisplayName = "Cohesion Test [GQL] - Values: malformed CASE identifies the offending token")]
    [InlineData("RETURN CASE END", "END")]
    [InlineData("RETURN CASE WHEN TRUE ELSE 1 END", "ELSE")]
    [InlineData("RETURN CASE 1 WHEN 1 2 END", "2")]
    public void Parse_MalformedCase_ShouldLocateToken(string source, string token)
    {
        // Act
        var error = new GqlQueryParser().Parse(source).Diagnostics.First();
        // Assert
        error.Code.ShouldBe("GQL0002");
        source[error.Start!.Value..error.End!.Value].ShouldBe(token);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Values: excluded collection syntax is a precise capability diagnostic")]
    [InlineData("[x IN [1] WHERE x > 0 | x]", "LIST COMPREHENSION")]
    [InlineData("[(a)->(b) | b]", "PATTERN COMPREHENSION")]
    [InlineData("[p=(a)->(b) | p]", "PATTERN COMPREHENSION")]
    [InlineData("[1,2][1..2]", "LIST SLICING")]
    [InlineData("a.b.c", "NESTED PROPERTY ACCESS")]
    public void Parse_ExcludedValueForm_ShouldNameConstruct(string expression, string construct)
    {
        // Act
        var diagnostics = new GqlQueryParser().Parse("MATCH (a) RETURN " + expression).Diagnostics;
        // Assert
        diagnostics.ShouldContain(item => item.Code == "COHDBL001" && item.Message!.Contains(construct));
    }
}
