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
    [InlineData("SELECT RAW ((SELECT RAW 1 FROM c) UNION (SELECT RAW 2 FROM d)) FROM e")]
    [InlineData("SELECT x FROM e WHERE x IN ((SELECT RAW 1 FROM c) UNION (SELECT RAW 2 FROM d))")]
    [InlineData("SELECT * FROM ((SELECT a FROM c) UNION (SELECT b FROM d)) AS q")]
    [InlineData("INSERT INTO c (KEY k) (SELECT k FROM d) UNION SELECT k FROM e")]
    [InlineData("UPSERT INTO c (KEY k) (SELECT k FROM d) UNION SELECT k FROM e")]
    [InlineData("SELECT RAW (SELECT RAW a FROM c INTERSECT SELECT RAW b FROM d) FROM e")]
    [InlineData("SELECT x FROM e WHERE x IN (SELECT RAW a FROM c INTERSECT SELECT RAW b FROM d)")]
    [InlineData("SELECT * FROM (SELECT a FROM c INTERSECT SELECT b FROM d) AS q")]
    [InlineData("INSERT INTO c (KEY k) SELECT k FROM d INTERSECT SELECT k FROM e")]
    [InlineData("UPSERT INTO c (KEY k) SELECT k FROM d INTERSECT SELECT k FROM e")]
    public void Parse_DeferredCompoundPosition_ShouldReportOneCapability(string text)
    {
        // Arrange / Act
        var statement = Parse(text);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Write sources: a lone parenthesized query is a query term")]
    [InlineData("INSERT")]
    [InlineData("UPSERT")]
    public void Parse_ParenthesizedWriteSource_ShouldAdmitSingleBlock(string verb)
    {
        // Arrange / Act
        var statement = Parse(verb + " INTO c (KEY k) (SELECT k FROM d)");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var source = statement.OqlExpression is OqlInsertExpression insert ? insert.Select : statement.OqlExpression.ShouldBeOfType<OqlUpsertExpression>().Select;
        source.ShouldBeOfType<OqlSelectExpression>().Collection.ShouldBe("d");
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Subqueries: a parenthesized term can still begin an expression or IN list")]
    [InlineData("SELECT RAW ((SELECT RAW 1 FROM c) = [1]) FROM d")]
    [InlineData("SELECT RAW 1 FROM c WHERE TRUE IN ((SELECT RAW 1 FROM d) = [1])")]
    [InlineData("SELECT RAW 1 FROM c WHERE [1] IN ((SELECT RAW 1 FROM d), [2])")]
    public void Parse_ParenthesizedSubqueryOperand_ShouldKeepExpressionGrammar(string text)
    {
        // Arrange / Act
        var statement = Parse(text);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Query expressions: the public variant family is closed and has two sealed leaves")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The untrimmed test host enumerates every shipped AST variant to verify family closure.")]
    public void QueryExpression_Variants_ShouldBeClosed()
    {
        // Arrange / Act
        var variants = typeof(OqlQueryExpression).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(OqlQueryExpression))).ToArray();
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

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Query tails: an outer refusal locates only its tail")]
    [InlineData("(SELECT a FROM c LIMIT 1) LIMIT 2", "LIMIT 2")]
    [InlineData("(SELECT a FROM c ORDER BY a) ORDER BY a", "ORDER BY a")]
    public void Parse_DuplicateOuterTail_ShouldLocateTail(string text, string tail)
    {
        // Arrange / Act
        var statement = Parse(text);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Start.ShouldBe(text.LastIndexOf(tail, StringComparison.Ordinal));
        diagnostic.End.ShouldBe(text.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Set operations: exactly 128 operators are admitted and the 129th is located")]
    [InlineData(128, false)]
    [InlineData(129, false)]
    [InlineData(128, true)]
    [InlineData(129, true)]
    public void Parse_OperatorDepthBoundary_ShouldLocateOffendingOperator(int operators, bool parenthesized)
    {
        // Arrange
        const string term = "SELECT a FROM c";
        string text = string.Join(" UNION ALL ", Enumerable.Repeat(term, operators + 1));
        if (parenthesized)
        { text = "(" + string.Join(" UNION ALL ", Enumerable.Repeat(term, 129)) + ")" + (operators == 129 ? " UNION ALL " + term : ""); }
        // Act
        var statement = Parse(text);
        // Assert
        if (operators == 128) { statement.Diagnostics.ShouldBeEmpty(); }
        else
        {
            var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CD00005");
            diagnostic.Start.ShouldBe(text.LastIndexOf("UNION", StringComparison.Ordinal));
            diagnostic.End.ShouldBe(diagnostic.Start + "UNION".Length);
        }
    }
}
