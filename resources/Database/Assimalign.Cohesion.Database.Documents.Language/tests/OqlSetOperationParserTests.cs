using System;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlSetOperationParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Set operations: every operator has equal left-associative precedence")]
    [InlineData("UNION", "UNION", OqlSetOperation.Union, OqlSetOperation.Union, 0)]
    [InlineData("UNION ALL", "INTERSECT ALL", OqlSetOperation.Union, OqlSetOperation.Intersect, 1)]
    [InlineData("EXCEPT", "UNION ALL", OqlSetOperation.Except, OqlSetOperation.Union, 1)]
    [InlineData("INTERSECT", "EXCEPT", OqlSetOperation.Intersect, OqlSetOperation.Except, 2)]
    public void Parse_OperatorChain_ShouldAssociateLeft(string first, string second, OqlSetOperation leftKind, OqlSetOperation rootKind, int refusals)
    {
        // Arrange / Act
        var statement = Parse($"SELECT a FROM c {first} SELECT b FROM d {second} SELECT e FROM f");
        // Assert
        statement.Diagnostics.Count().ShouldBe(refusals);
        statement.Diagnostics.ShouldAllBe(diagnostic => diagnostic.Code == "CK00001");
        var root = statement.OqlExpression.ShouldBeOfType<OqlSetOperationExpression>();
        root.Operation.ShouldBe(rootKind);
        root.Right.ShouldBeOfType<OqlSelectExpression>().Collection.ShouldBe("f");
        var left = root.Left.ShouldBeOfType<OqlSetOperationExpression>();
        left.Operation.ShouldBe(leftKind);
        left.Left.ShouldBeOfType<OqlSelectExpression>().Collection.ShouldBe("c");
        left.Right.ShouldBeOfType<OqlSelectExpression>().Collection.ShouldBe("d");
        root.IsAll.ShouldBe(second.EndsWith("ALL", StringComparison.Ordinal));
        left.IsAll.ShouldBe(first.EndsWith("ALL", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - UNION: parentheses own local ordering and pages")]
    public void Parse_ParenthesizedOperands_ShouldRetainIndependentTails()
    {
        // Arrange / Act
        var statement = Parse("(SELECT a AS n FROM c ORDER BY a LIMIT 2 OFFSET 1) UNION ALL (SELECT b AS n FROM d ORDER BY b DESC OFFSET 3 LIMIT 4) ORDER BY n DESC NULLS FIRST LIMIT 5 OFFSET 6");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var compound = statement.OqlExpression.ShouldBeOfType<OqlSetOperationExpression>();
        compound.OrderBy.Single().NullPlacement.ShouldBe(OqlNullPlacement.First);
        compound.Limit.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<JsonElement>().GetInt64().ShouldBe(5L);
        compound.Offset.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<JsonElement>().GetInt64().ShouldBe(6L);
        var left = compound.Left.ShouldBeOfType<OqlSelectExpression>();
        left.OrderBy.Single().Descending.ShouldBeFalse();
        left.Limit.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<JsonElement>().GetInt64().ShouldBe(2L);
        var right = compound.Right.ShouldBeOfType<OqlSelectExpression>();
        right.OrderBy.Single().Descending.ShouldBeTrue();
        right.Offset.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<JsonElement>().GetInt64().ShouldBe(3L);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - UNION: unparenthesized local tails and malformed operands are syntax errors")]
    [InlineData("SELECT a FROM c LIMIT 1 UNION SELECT b FROM d")]
    [InlineData("SELECT a FROM c LIMIT 1 UNION SELECT b,c FROM d")]
    [InlineData("SELECT a FROM c ORDER BY a UNION SELECT b FROM d")]
    [InlineData("SELECT a FROM c OFFSET 1 UNION SELECT b FROM d")]
    [InlineData("SELECT a FROM c UNION")]
    [InlineData("SELECT a FROM c UNION (SELECT b FROM d")]
    public void Parse_InvalidCompound_ShouldRefuseSyntax(string text)
    {
        // Arrange / Act
        var statement = Parse(text);
        // Assert
        statement.Diagnostics.ShouldNotBeEmpty();
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CD00002");
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - UNION: nested and DML positions retain the next-run capability gate")]
    [InlineData("(SELECT a FROM c LIMIT 1) LIMIT 2")]
    [InlineData("(SELECT a FROM c UNION SELECT b FROM d LIMIT 1) OFFSET 2")]
    [InlineData("SELECT RAW (SELECT a FROM c UNION SELECT b FROM d) FROM c")]
    [InlineData("SELECT * FROM (SELECT a FROM c UNION SELECT b FROM d) AS q")]
    [InlineData("INSERT INTO c (KEY k) SELECT k FROM c UNION SELECT k FROM d")]
    [InlineData("UPSERT INTO c (KEY k) SELECT k FROM c UNION SELECT k FROM d")]
    public void Parse_DeferredCompoundPosition_ShouldReportOneCapability(string text)
    {
        // Arrange / Act
        var statement = Parse(text);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Query expressions: the public variant family is closed and has two sealed leaves")]
    public void QueryExpression_Variants_ShouldBeClosed()
    {
        // Arrange / Act
        var variants = new[] { typeof(OqlSelectExpression), typeof(OqlSetOperationExpression) };
        // Assert
        variants.Select(type => type.Name).Order().ShouldBe(new[] { nameof(OqlSelectExpression), nameof(OqlSetOperationExpression) });
        variants.ShouldAllBe(type => type.IsSealed && type.BaseType == typeof(OqlQueryExpression));
        typeof(OqlQueryExpression).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .ShouldHaveSingleItem().IsFamilyAndAssembly.ShouldBeTrue();
        typeof(OqlSubqueryExpression).GetProperty(nameof(OqlSubqueryExpression.Query))!.PropertyType.ShouldBe(typeof(OqlQueryExpression));
        typeof(OqlInsertExpression).GetProperty(nameof(OqlInsertExpression.Select))!.PropertyType.ShouldBe(typeof(OqlQueryExpression));
        typeof(OqlUpsertExpression).GetProperty(nameof(OqlUpsertExpression.Select))!.PropertyType.ShouldBe(typeof(OqlQueryExpression));
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Set operations: query parentheses and long operator chains obey the nesting guard")]
    public void Parse_ExcessiveQueryDepth_ShouldRefuseWithoutRecursingUnboundedly()
    {
        // Arrange
        string parenthesized = new string('(', 256) + "SELECT a FROM c" + new string(')', 256);
        string chain = string.Join(" UNION ALL ", Enumerable.Repeat("SELECT a FROM c", 257));
        // Act / Assert
        foreach (string text in new[] { parenthesized, chain })
        { Parse(text).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CD00005"); }
    }

    private static OqlQueryStatement Parse(string text) => (OqlQueryStatement)new OqlQueryParser().Parse(text);
}
