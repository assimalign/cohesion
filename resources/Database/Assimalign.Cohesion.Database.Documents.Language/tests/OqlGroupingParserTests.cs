using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlGroupingParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Grouping: LETTING remains a contextual identifier outside its deferred clause")]
    [InlineData("SELECT letting.a FROM items letting")]
    [InlineData("SELECT letting FROM items GROUP BY letting")]
    [InlineData("SELECT g FROM items GROUP BY a AS letting GROUP AS g")]
    public void Parse_ContextualLetting_ShouldPreserveIdentifiers(string text)
    {
        // Arrange / Act / Assert
        new OqlQueryParser().Parse(text).Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - GROUP AS: group terms retain explicit implicit and quoted aliases")]
    [InlineData("AS key")]
    [InlineData("key")]
    [InlineData("AS \"key\"")]
    public void Parse_GroupBindings_ShouldRetainAliases(string alias)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT g FROM items d GROUP BY d.a " + alias + ", d.b GROUP AS g HAVING ARRAY_LENGTH(g) > 0 ORDER BY key");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var query = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        query.GroupBy.Count.ShouldBe(2);
        query.GroupAliases.ShouldBe(new string?[] { "key", null });
        query.GroupAs.ShouldBe("g");
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - GROUP AS: malformed forms refuse once with a located diagnostic")]
    [InlineData("SELECT g FROM items GROUP AS g", "CD00012")]
    [InlineData("SELECT g FROM items GROUP BY a GROUP AS", "CD00002")]
    [InlineData("SELECT g FROM items GROUP BY a AS", "CD00002")]
    [InlineData("SELECT g FROM items GROUP BY a GROUP g", "CD00002")]
    [InlineData("SELECT g FROM items GROUP BY a GROUP AS g GROUP AS h", "CD00002")]
    [InlineData("SELECT a FROM items GROUP BY a LETTING x = 1", "CK00001")]
    public void Parse_InvalidGrouping_ShouldRefuseOnce(string text, string code)
    {
        // Arrange / Act
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldNotBeNull();
        diagnostic.End.ShouldNotBeNull();
    }
}
