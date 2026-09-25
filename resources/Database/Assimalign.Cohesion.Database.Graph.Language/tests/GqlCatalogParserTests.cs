using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlCatalogParserTests
{
    [Theory]
    [InlineData("SHOW LABELS", GqlCatalogSurface.Labels)]
    [InlineData("show relationship types;", GqlCatalogSurface.RelationshipTypes)]
    [InlineData("SHOW /* metadata */ PROPERTY KEYS", GqlCatalogSurface.PropertyKeys)]
    [InlineData("SHOW INDEXES", GqlCatalogSurface.Indexes)]
    [InlineData("SHOW OBJECT OWNERSHIP", GqlCatalogSurface.ObjectOwnership)]
    public void CatalogStatementsHaveDedicatedAstWithoutGraphPatterns(string command, GqlCatalogSurface subject)
    {
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse(command);
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.CatalogSurface.ShouldBe(subject);
        statement.GqlExpression.Matches.ShouldBeEmpty();
        statement.GqlExpression.Creates.ShouldBeEmpty();
        GqlLanguageProfile.Instance.Supports(GqlClauses.Show).ShouldBeTrue();
    }

    [Theory]
    [InlineData("SHOW LABELS DELETE n")]
    [InlineData("SHOW PROPERTY KEYS SET key.name = 'x'")]
    [InlineData("SHOW RELATIONSHIP TYPES CREATE (:Injected)")]
    [InlineData("SHOW INDEXES DROP INDEX by_name")]
    [InlineData("SHOW OBJECT OWNERSHIP REMOVE owner")]
    public void MutationCompositionHasStableReadOnlyDiagnostic(string command)
    {
        var statement = new GqlQueryParser().Parse(command);
        var error = statement.Diagnostics.Single();
        error.Code.ShouldBe("GQL0007");
        error.Message.ShouldBe("Graph catalog introspection is read-only.");
    }

    [Theory]
    [InlineData("SHOW DATABASES")]
    [InlineData("SHOW LABELS FROM other")]
    [InlineData("SHOW other.LABELS")]
    [InlineData("SHOW LABELS RETURN label")]
    [InlineData("SHOW PROPERTY")]
    [InlineData("SHOW LABELS; SHOW INDEXES")]
    public void CatalogGrammarRefusesScopeSelectionAndUnsupportedComposition(string command)
    {
        new GqlQueryParser().Parse(command).Diagnostics.ShouldNotBeEmpty();
    }
}
