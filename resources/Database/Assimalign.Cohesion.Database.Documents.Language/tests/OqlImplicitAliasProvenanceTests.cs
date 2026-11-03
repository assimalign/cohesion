using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlImplicitAliasProvenanceTests
{
    [Theory(DisplayName = "Cohesion Test [OQL] - Implicit aliases: FROM and UNNEST retain inferred-name provenance")]
    [InlineData("SELECT * FROM items UNNEST tags", true, true)]
    [InlineData("SELECT * FROM items AS items UNNEST tags AS tags", false, false)]
    [InlineData("SELECT * FROM items items UNNEST tags tags", false, false)]
    [InlineData("SELECT RAW (SELECT * FROM d.tags UNNEST tags.values) FROM items d", true, true)]
    [InlineData("SELECT RAW (SELECT * FROM d.tags AS tags UNNEST tags.values AS t) FROM items d", false, false)]
    public void Parse_SourceNames_ShouldRetainDeclarationKind(string text, bool implicitFrom, bool implicitUnnest)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var query = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        if (query.Projections[0].Expression is OqlSubqueryExpression nested) { query = nested.Query.ShouldBeOfType<OqlSelectExpression>(); }
        query.IsAliasImplicit.ShouldBe(implicitFrom);
        var unnest = query.Unnest.ShouldHaveSingleItem();
        (unnest.Alias is null).ShouldBe(implicitUnnest);
        unnest.Variable.ShouldNotBeNullOrEmpty();
    }
}
