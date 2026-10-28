using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlOrderingParserTests
{
    [Theory(DisplayName = "Cohesion Test [OQL] - ORDER BY retains direction and explicit null placement (#1730)")]
    [InlineData("", false, OqlNullPlacement.Default)]
    [InlineData("ASC", false, OqlNullPlacement.Default)]
    [InlineData("DESC", true, OqlNullPlacement.Default)]
    [InlineData("ASC NULLS FIRST", false, OqlNullPlacement.First)]
    [InlineData("ASC NULLS LAST", false, OqlNullPlacement.Last)]
    [InlineData("DESC NULLS FIRST", true, OqlNullPlacement.First)]
    [InlineData("DESC NULLS LAST", true, OqlNullPlacement.Last)]
    [InlineData("nulls last", false, OqlNullPlacement.Last)]
    public void Parse_OrderingSuffix_ShouldRetainWrittenPlacement(string suffix, bool descending, OqlNullPlacement placement)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse($"SELECT v AS k FROM c ORDER BY k {suffix}, n DESC NULLS LAST LIMIT 2 OFFSET 1");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var query = statement.ShouldBeOfType<OqlQueryStatement>().OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        query.OrderBy.Count.ShouldBe(2);
        query.OrderBy[0].Descending.ShouldBe(descending);
        query.OrderBy[0].NullPlacement.ShouldBe(placement);
        query.OrderBy[1].NullPlacement.ShouldBe(OqlNullPlacement.Last);
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Malformed NULLS receives its located syntax identity (#1730)")]
    [InlineData("SELECT v FROM c ORDER BY v NULLS", "CD00013", "", 1)]
    [InlineData("SELECT v FROM c ORDER BY v NULLS MIDDLE", "CD00013", "MIDDLE", 1)]
    [InlineData("SELECT v FROM c ORDER BY v NULLS , n", "CD00013", ",", 1)]
    [InlineData("SELECT v FROM c ORDER BY v NULLS ALL", "CD00013", "ALL", 1)]
    [InlineData("SELECT v FROM c ORDER BY v NULLS DISTINCT", "CD00013", "DISTINCT", 1)]
    [InlineData("SELECT v FROM c ORDER BY v NULLS 1", "CD00013", "1", 1)]
    [InlineData("SELECT v FROM c ORDER BY v NULLS FIRST LAST", "CK00001", "LAST", 1)]
    [InlineData("SELECT v FROM c ORDER BY v NULLS\r\nALL", "CD00013", "ALL", 2)]
    public void Parse_InvalidPlacement_ShouldLocateExpectedOption(string text, string code, string token, int line)
    {
        // Arrange
        int start = token.Length == 0 ? text.Length : text.IndexOf(token, StringComparison.Ordinal);

        // Act
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(start + token.Length);
        diagnostic.Line.ShouldBe(line);
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - NULLS remains a contextual name (#1730)")]
    [InlineData("SELECT nulls FROM c")]
    [InlineData("SELECT c.nulls AS nulls FROM c")]
    [InlineData("SELECT v FROM nulls")]
    [InlineData("SELECT nulls.v FROM c AS nulls")]
    [InlineData("SELECT nulls.v FROM c nulls")]
    [InlineData("SELECT v FROM c ORDER BY nulls")]
    [InlineData("SELECT v FROM c ORDER BY nulls NULLS LAST")]
    public void Parse_NullsIdentifier_ShouldKeepContextualMeaning(string text)
    {
        // Arrange
        var parser = new OqlQueryParser();

        // Act
        var statement = parser.Parse(text);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - INCLUDE MISSING attaches only to the leading scalar key (#1632)")]
    public void Parse_IncludeMissing_ShouldRetainKeyLocalOptionAndContextualNames()
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse("CREATE INDEX ix ON c (a INCLUDE MISSING, b)");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var create = statement.ShouldBeOfType<OqlQueryStatement>().OqlExpression.ShouldBeOfType<OqlCreateIndexExpression>();
        create.IncludeMissing.ShouldBeTrue();
        create.Paths.Count.ShouldBe(2);
        foreach (string query in new[] { "SELECT nulls, include FROM c", "SELECT v FROM c ORDER BY nulls", "CREATE INDEX ix ON c (include)" })
        { new OqlQueryParser().Parse(query).Diagnostics.ShouldBeEmpty(); }
        new OqlQueryParser().Parse("CREATE INDEX ix ON c (a, b INCLUDE MISSING)").Diagnostics.Single().Code.ShouldBe("CD00002");
        new OqlQueryParser().Parse("CREATE INDEX ix ON c (a INCLUDE)").Diagnostics.Single().Code.ShouldBe("CD00002");
    }
}
