using System;
using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlCompositeIndexParserTests
{
    [Fact(DisplayName = "Cohesion Test [OQL] - Composite UNIQUE index modifiers and ordered paths (#1135)")]
    public void Parse_CompositeUniqueIndex_ShouldKeepOrderedPathsAndModifiers()
    {
        // Arrange / Act.
        var statement = new OqlQueryParser().Parse("CREATE UNIQUE INDEX IF NOT EXISTS ix ON c (a, b.c, d[0], ['x.y'])");

        // Assert.
        statement.Diagnostics.ShouldBeEmpty();
        var create = statement.ShouldBeOfType<OqlQueryStatement>().OqlExpression.ShouldBeOfType<OqlCreateIndexExpression>();
        create.IsUnique.ShouldBeTrue(); create.IfNotExists.ShouldBeTrue();
        create.Paths.Count.ShouldBe(4); create.Path.ShouldBeSameAs(create.Paths[0]);
        create.Paths[1].Segments.ShouldBe([new("b", null), new("c", null)]);
        create.Paths[2].Segments.ShouldBe([new("d", null), new(null, 0)]);
        create.Paths[3].Segments.ShouldBe([new("x.y", null)]);
        var quoted = new OqlQueryParser().Parse("CREATE INDEX \"if\" ON c (a)").ShouldBeOfType<OqlQueryStatement>();
        quoted.Diagnostics.ShouldBeEmpty();
        quoted.OqlExpression.ShouldBeOfType<OqlCreateIndexExpression>().IndexName.ShouldBe("if");
        var drop = new OqlQueryParser().Parse("DROP INDEX IF EXISTS ix ON c").ShouldBeOfType<OqlQueryStatement>();
        drop.Diagnostics.ShouldBeEmpty();
        drop.OqlExpression.ShouldBeOfType<OqlDropIndexExpression>().IfExists.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Invalid index keys have one exact diagnostic (#1135)")]
    [InlineData("CREATE INDEX ix ON c (a, a)", "CD00002", "a", 25)]
    [InlineData("CREATE INDEX ix ON c (a, ['a'])", "CD00002", "[", 25)]
    [InlineData("CREATE INDEX ix ON c ()", "CD00002", ")", 22)]
    [InlineData("CREATE INDEX ix ON c (ALL tags)", "CK00001", "ALL", 22)]
    [InlineData("CREATE INDEX ix ON c (tags[*])", "CD00002", "*", 27)]
    [InlineData("CREATE INDEX ix ON c (a DESC)", "CD00002", "DESC", 24)]
    [InlineData("CREATE INDEX ix ON c (LOWER(a))", "CD00002", "(", 27)]
    [InlineData("CREATE INDEX ix ON c (a) WHERE a > 1", "CD00002", "WHERE", 25)]
    [InlineData("CREATE INDEX ix ON c (a) INCLUDE MISSING", "CD00002", "INCLUDE", 25)]
    public void Parse_InvalidIndexKey_ShouldReportExactToken(string text, string code, string token, int start)
    {
        // Arrange / Act.
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();

        // Assert.
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(start);
        text.Substring(start, token.Length).ShouldBe(token);
        if (code == "CK00001") { diagnostic.Message!.ShouldContain(token + (token == "DISTINCT" ? " ARRAY" : "")); }
    }
    [Fact(DisplayName = "Cohesion Test [OQL] - Large malformed array keys retain one syntax refusal (#1530)")]
    public void Parse_LargeMalformedIndexKey_ShouldRejectOnceAtSecondBinding()
    {
        // Arrange: capability validation must not rescan the entire key prefix per word.
        string text = "CREATE INDEX ix ON c (DISTINCT ARRAY t " + string.Join(' ', Enumerable.Repeat("FOR t IN tags", 20000)) + " END)";

        // Act.
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();

        // Assert.
        diagnostic.Code.ShouldBe("CD00002");
        diagnostic.Start.ShouldBe(text.IndexOf("FOR", text.IndexOf("FOR", StringComparison.Ordinal) + 1, StringComparison.Ordinal));
    }

}
