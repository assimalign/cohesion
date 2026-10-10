using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// Measures parse strictness (#1101) against a running graph engine: a stray character and an
/// ISO construct outside the subset fail at parse time on both session seams, and no mutation
/// among them changes the graph.
/// </summary>
public sealed class GqlParseStrictnessExecutionTests
{
    private const string Seed = "INSERT (a:Person {name: 'Alice'})-[r:KNOWS]->(b:Person {name: 'Bob'})";

    /// <param name="gql">A statement with a stray character or an unsupported ISO construct.</param>
    /// <param name="code">The single error code it reports.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Strictness: stray characters and ISO constructs fail at parse time and execute nothing")]
    [InlineData("MATCH (a) RETURN a.x ?", "GQL0002")]
    [InlineData("MATCH (a:Person) WHERE a.name = ? DELETE a", "GQL0002")]
    [InlineData("MATCH (a:Person) DETACH DELETE a #", "GQL0002")]
    [InlineData("MATCH (n:Person) NODETACH DELETE n", "COHDBL001")]
    // #1139 made (n:Person|Robot) and (n IS Person) executable; their places go to the edges it
    // rejects: an undirected tilde edge and a Cypher arrow that used to truncate the statement.
    [InlineData("MATCH (n:Person)~(m) DETACH DELETE n", "COHDBL001")]
    [InlineData("MATCH (n:Person)-->(m)\nDETACH DELETE n", "GQL0008")]
    [InlineData("MATCH TRAIL (a)-[r]->(b) DETACH DELETE a", "COHDBL001")]
    [InlineData("MATCH p = ACYCLIC (a)-[r]->(b) RETURN p", "COHDBL001")]
    [InlineData("MATCH ALL SHORTEST (a)-[r]->(b) DETACH DELETE b", "COHDBL001")]
    [InlineData("MERGE (n:Person {name: 'Cara'})", "COHDBL001")]
    public async Task Execute_StrictnessViolation_FailsAtParseTimeWithoutEffectAsync(string gql, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
        var database = await engine.CreateDatabaseAsync("strictness", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(Seed, cancellationToken: CancellationToken.None);
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse(gql);

        // Act
        var text = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None));
        var typed = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(new GraphQueryRequest(statement), CancellationToken.None));

        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        text.Message.ShouldStartWith($"GQL parse error {code}: ", Case.Sensitive);
        typed.Message.ShouldBe(text.Message);
        (await NamesAsync(session, "MATCH (a:Person) RETURN a.name")).ShouldBe(["Alice", "Bob"], ignoreOrder: true);
        (await NamesAsync(session, "MATCH (a)-[r:KNOWS]->(b) RETURN b.name")).ShouldBe(["Bob"]);
    }

    private static async Task<List<string?>> NamesAsync(GraphDatabaseSession session, string gql)
    {
        await using var result = (QueryResultSet)await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None);
        var names = new List<string?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            names.Add(row.IsNull(0) ? null : row.GetString(0));
        }
        return names;
    }
}
