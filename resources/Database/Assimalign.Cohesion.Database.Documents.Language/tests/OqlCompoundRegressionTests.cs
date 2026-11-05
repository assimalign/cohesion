using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlCompoundRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Compounds: operator depth crosses SELECT subqueries and locates level 129")]
    [InlineData(64, 64, false)]
    [InlineData(64, 65, false)]
    [InlineData(127, 1, true)]
    [InlineData(128, 1, true)]
    [InlineData(128, 0, false)]
    [InlineData(128, 1, false)]
    public void Parse_SubqueryOperatorDepth_ShouldSharePathBound(int inner, int outer, bool onRight)
    {
        // Arrange: a tail copy of the SELECT leaf must preserve its nested depth too.
        const string term = "SELECT RAW 1 FROM [0] AS z";
        string nested = "(SELECT RAW (" + string.Join(" UNION ALL ", Enumerable.Repeat(term, inner + 1)) + ") FROM [0] AS a LIMIT 1)";
        string text = onRight ? string.Join(" UNION ALL ", Enumerable.Repeat(term, outer).Append(nested)) :
            string.Join(" UNION ALL ", Enumerable.Repeat(term, outer).Prepend(nested));
        // Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(text);
        // Assert
        if (inner + outer <= 128) { statement.Diagnostics.ShouldBeEmpty(); }
        else
        {
            var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CD00005");
            diagnostic.Start.ShouldBe(onRight ? text.IndexOf(" UNION ALL (SELECT", StringComparison.Ordinal) + 1 : text.LastIndexOf("UNION", StringComparison.Ordinal));
            diagnostic.End.ShouldBe(diagnostic.Start + 5);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Compounds: reserved projection labels restore the enclosing SELECT scope")]
    [InlineData("last")]
    [InlineData("set")]
    public void Parse_ReservedLabelAfterCompound_ShouldRestoreScope(string label)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT (SELECT RAW 1 FROM c UNION SELECT RAW 2 FROM d) AS " + label + " FROM e");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections.Single().Alias.ShouldBe(label);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Compounds: a nested SELECT does not hide grouped LETTING refusal")]
    public void Parse_LettingAfterCompound_ShouldKeepCapabilityCode()
    {
        // Arrange
        const string text = "SELECT a FROM e GROUP BY a HAVING a IN (SELECT RAW 1 FROM c UNION SELECT RAW 2 FROM d) LETTING x = 1";
        // Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(text);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Start.ShouldBe(text.IndexOf("LETTING", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - IN: a wrapped compound value differs from a parenthesis-led compound query")]
    [InlineData("((SELECT RAW 1 FROM c UNION SELECT RAW 3 FROM d))", false)]
    [InlineData("(((SELECT RAW 1 FROM c) UNION SELECT RAW 3 FROM d))", true)]
    public void Parse_DoublyWrappedCompound_ShouldPinMembershipReading(string operand, bool queryMembership)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT RAW x FROM [1,2,3] AS x WHERE x IN " + operand);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var membership = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Predicate.ShouldBeOfType<OqlInExpression>();
        var subquery = queryMembership ? membership.Array.ShouldBeOfType<OqlSubqueryExpression>() :
            membership.Values.ShouldHaveSingleItem().ShouldBeOfType<OqlSubqueryExpression>();
        if (queryMembership) { membership.Values.ShouldBeEmpty(); }
        else { membership.Array.ShouldBeNull(); }
        subquery.Query.ShouldBeOfType<OqlSetOperationExpression>().Operation.ShouldBe(OqlSetOperation.Union);
    }
}
