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
    [InlineData("CREATE INDEX ix ON c (a, a)", "OQL0002", "a", 25)]
    [InlineData("CREATE INDEX ix ON c (a, ['a'])", "OQL0002", "[", 25)]
    [InlineData("CREATE INDEX ix ON c ()", "OQL0002", ")", 22)]
    [InlineData("CREATE INDEX ix ON c (ALL tags)", "COHDBL001", "ALL", 22)]
    [InlineData("CREATE INDEX ix ON c (DISTINCT ARRAY t FOR t IN tags END)", "COHDBL001", "DISTINCT", 22)]
    [InlineData("CREATE INDEX ix ON c (tags[*])", "OQL0002", "*", 27)]
    [InlineData("CREATE INDEX ix ON c (a DESC)", "OQL0002", "DESC", 24)]
    [InlineData("CREATE INDEX ix ON c (LOWER(a))", "OQL0002", "(", 27)]
    [InlineData("CREATE INDEX ix ON c (a) WHERE a > 1", "OQL0002", "WHERE", 25)]
    [InlineData("CREATE INDEX ix ON c (a) INCLUDE MISSING", "OQL0002", "INCLUDE", 25)]
    public void Parse_InvalidIndexKey_ShouldReportExactToken(string text, string code, string token, int start)
    {
        // Arrange / Act.
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();

        // Assert.
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(start);
        text.Substring(start, token.Length).ShouldBe(token);
        if (code == "COHDBL001") { diagnostic.Message!.ShouldContain(token + (token == "DISTINCT" ? " ARRAY" : "")); }
    }
}
