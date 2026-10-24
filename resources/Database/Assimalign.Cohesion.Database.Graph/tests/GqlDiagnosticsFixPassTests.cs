using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlDiagnosticsFixPassTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Diagnostics: catalog writes carry one unprefixed code and the query span")]
    public async Task Execute_TypedCatalogWrite_ShouldCarryLocatedDiagnosticAsync()
    {
        // Arrange
        var parsed = GraphQueryRequest.FromGql("INSERT (a:Item)").Statement.GqlExpression;
        var query = new GqlQueryExpression(parsed.Matches, parsed.Predicate, parsed.Creates, parsed.DeleteVariables,
            parsed.DetachDelete, parsed.Projections, parsed.Location) { CatalogSurface = GqlCatalogSurface.Labels };
        await using var engine = GraphDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CG00007");
        diagnostic.Start.ShouldBe(query.Location!.Start);
        diagnostic.End.ShouldBe(query.Location.End);
        failure.Message.ShouldBe("Graph catalog introspection is read-only.");
        diagnostic.Message.ShouldBe(failure.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Diagnostics: invalid path requests retain the query span")]
    public async Task Execute_InvalidPathProjection_ShouldCarryQuerySpanAsync()
    {
        // Arrange
        var request = GraphPathsQueryRequest.FromGql("MATCH (a) RETURN a.x");
        await using var engine = GraphDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CG05001");
        diagnostic.Start.ShouldBe(request.Statement.GqlExpression.Location!.Start);
        diagnostic.End.ShouldBe(request.Statement.GqlExpression.Location.End);
    }
}
