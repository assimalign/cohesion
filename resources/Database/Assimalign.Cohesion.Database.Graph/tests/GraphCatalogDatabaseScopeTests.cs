using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphCatalogDatabaseScopeTests
{
    [Fact]
    public async Task EveryCatalogSubjectIsBoundToTheSessionDatabase()
    {
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
        var own = await engine.CreateDatabaseAsync("own");
        var other = await engine.CreateDatabaseAsync("other");
        await using var session = await own.CreateSessionAsync();
        await using var otherSession = await other.CreateSessionAsync();
        await otherSession.ExecuteAsync("INSERT (:Secret {value: 1})-[:PRIVATE {weight: 2}]->(:Secret)");
        await GraphSchema.Open(other, otherSession).CreateIndexAsync("Secret", "private_index", "value");

        foreach (string subject in GraphCatalogIntrospectionTests.Subjects)
        {
            (await GraphCatalogIntrospectionTests.Rows(session, "SHOW " + subject)).ShouldBeEmpty();
            var otherRows = await GraphCatalogIntrospectionTests.Rows(otherSession, "SHOW " + subject);
            otherRows.ShouldNotBeEmpty();
            otherRows.All(row => Equals(row[0], "other")).ShouldBeTrue();
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"SHOW {subject} FROM other"));
        }
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SHOW DATABASES"));
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SHOW other.LABELS"));
        session.Database.ShouldBeSameAs(own);
    }
}
