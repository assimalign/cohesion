using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlComprehensionParserTests
{
    [Theory(DisplayName = "Cohesion Test [OQL] - Comprehensions: retain mapping binding predicate and span")]
    [InlineData("ARRAY", false)]
    [InlineData("FIRST", true)]
    public void Parse_Comprehension_ShouldRetainTree(string keyword, bool first)
    {
        // Arrange
        string expression = $"{keyword} (x + 1) FOR x IN d.tags WHEN x > 0 END";
        // Act
        var statement = Parse($"SELECT {expression} FROM items d");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var node = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections[0].Expression.ShouldBeOfType<OqlComprehensionExpression>();
        node.Variable.ShouldBe("x");
        node.IsFirst.ShouldBe(first);
        node.Projection.ShouldBeOfType<OqlBinaryExpression>().Operator.ShouldBe("+");
        node.Collection.ShouldBeOfType<OqlPathExpression>().Segments.Count.ShouldBe(2);
        node.Predicate.ShouldBeOfType<OqlBinaryExpression>().Operator.ShouldBe(">");
        node.Location!.Start.ShouldBe(7);
        node.Location.End.ShouldBe(7 + expression.Length);
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Comprehensions: compose with all expression contexts")]
    [InlineData("SELECT ARRAY FIRST x FOR x IN y END FOR y IN d.tags END FROM items d")]
    [InlineData("SELECT ARRAY CASE WHEN x THEN x ELSE NULL END FOR x IN [1] WHEN ANY y IN [x] SATISFIES y END END FROM c")]
    [InlineData("SELECT ANY x IN [1] SATISFIES ARRAY y FOR y IN [x] END IN [[1]] END FROM c")]
    [InlineData("SELECT ARRAY (x) + 1 FOR x IN [1] END FROM c")]
    [InlineData("SELECT ARRAY x FOR \"x\" IN [1] END FROM c")]
    [InlineData("UPDATE c SET a = ARRAY x FOR x IN tags END RETURNING FIRST x FOR x IN a END")]
    [InlineData("DELETE FROM c WHERE FIRST x FOR x IN tags END = 1 RETURNING ARRAY x FOR x IN tags END")]
    [InlineData("INSERT INTO c (KEY, VALUE) VALUES ('a', ARRAY x FOR x IN [1] END) RETURNING VALUE ARRAY x FOR x IN [1] END")]
    public void Parse_ExpressionContext_ShouldParse(string text)
    {
        // Arrange / Act
        var statement = Parse(text);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Comprehensions: lookahead balances END-delimited mapping and WHEN expressions")]
    [InlineData("(x) + CASE WHEN TRUE THEN 1 ELSE 2 END", "CASE WHEN TRUE THEN TRUE ELSE FALSE END")]
    [InlineData("(x) IN ARRAY y FOR y IN [x] END", "ARRAY y FOR y IN [x] END")]
    [InlineData("(x) + FIRST y FOR y IN [x] END", "FIRST y FOR y IN [x] END")]
    [InlineData("(x) = ANY y IN [x] SATISFIES y END", "ANY y IN [x] SATISFIES y END")]
    [InlineData("(x) = EVERY y IN [x] SATISFIES y END", "EVERY y IN [x] SATISFIES y END")]
    [InlineData("(x) + x.end + x.from + x.as", "x.case")]
    public void Parse_EndDelimitedChildren_ShouldBalanceLookahead(string mapping, string condition)
    {
        // Arrange / Act
        foreach (string keyword in new[] { "ARRAY", "FIRST" })
        {
            var statement = Parse($"SELECT {keyword} {mapping} FOR x IN [1] WHEN {condition} END AS result FROM items");
            // Assert
            statement.Diagnostics.ShouldBeEmpty();
            var node = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections[0].Expression.ShouldBeOfType<OqlComprehensionExpression>();
            node.IsFirst.ShouldBe(keyword == "FIRST");
            node.Variable.ShouldBe("x");
            node.Predicate.ShouldNotBeNull();
        }
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Comprehensions: malformed and deferred forms locate one diagnostic")]
    [InlineData("SELECT ARRAY «FOR» x IN [1] END FROM c", "CD00002")]
    [InlineData("SELECT ARRAY «FROM» c", "CD00002")]
    [InlineData("SELECT FIRST «FROM» c", "CD00002")]
    [InlineData("SELECT ARRAY x FOR «IN» [1] END FROM c", "CD00002")]
    [InlineData("SELECT FIRST x FOR x IN «WHEN» TRUE END FROM c", "CD00002")]
    [InlineData("SELECT ARRAY x FOR x IN [1] WHEN «END» FROM c", "CD00002")]
    [InlineData("SELECT ARRAY x FOR x IN [1] «FROM» c", "CD00002")]
    [InlineData("SELECT ARRAY x FOR x IN [1]«,» y IN [2] END FROM c", "CK00001")]
    [InlineData("SELECT FIRST x FOR i«:»x IN [1] END FROM c", "CK00001")]
    [InlineData("SELECT ARRAY x FOR x «WITHIN» [1] END FROM c", "CK00001")]
    [InlineData("SELECT ARRAY («SELECT» x FROM c) FOR x IN [1] END FROM c", "CK00001")]
    public void Parse_InvalidComprehension_ShouldLocateFailure(string marked, string code)
    {
        // Arrange
        int start = marked.IndexOf('«');
        int end = marked.IndexOf('»') - 1;
        string text = marked.Replace("«", "", StringComparison.Ordinal).Replace("»", "", StringComparison.Ordinal);
        // Act
        var statement = Parse(text);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(end);
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Comprehensions: retired ARRAY and FIRST functions retain CK00001")]
    [InlineData("ARRAY", "(1)")]
    [InlineData("FIRST", "(1)")]
    [InlineData("ARRAY", "(FIRST x FOR x IN [1] END)")]
    [InlineData("FIRST", "(ARRAY x FOR x IN [1] END)")]
    [InlineData("ARRAY", "(1) + FIRST y FOR y IN [1] END")]
    [InlineData("FIRST", "(1) + ARRAY y FOR y IN [1] END")]
    public void Parse_RetiredComprehensionCalls_ShouldRefuseFunctions(string keyword, string arguments)
    {
        // Arrange / Act
        var statement = Parse($"SELECT {keyword}{arguments} FROM c");
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message.ShouldNotBeNull().ShouldContain($"The {keyword} clause", Case.Sensitive);
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(7 + keyword.Length);
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Comprehensions: parser depth guard covers nested mappings")]
    [InlineData(150, false)]
    [InlineData(20000, true)]
    public void Parse_DeepComprehension_ShouldRefuseAtDepthLimit(int depth, bool parenthesized)
    {
        // Arrange
        string expression = string.Concat(Enumerable.Repeat(parenthesized ? "ARRAY (" : "ARRAY ", depth)) + "x" +
            string.Concat(Enumerable.Repeat(parenthesized ? ") FOR x IN [1] END" : " FOR x IN [1] END", depth));
        // Act
        var statement = Parse($"SELECT {expression} FROM c");
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00005");
    }

    private static OqlQueryStatement Parse(string text) => (OqlQueryStatement)new OqlQueryParser().Parse(text);
}
