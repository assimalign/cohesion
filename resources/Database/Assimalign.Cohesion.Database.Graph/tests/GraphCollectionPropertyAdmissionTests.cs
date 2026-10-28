using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphCollectionPropertyAdmissionTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE: indexed comprehension equalities evaluate once per input row")]
    public async Task ExecuteAsync_MergeEquality_ShouldUseEachIncomingListAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("collection-merge", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["l"] = new[] { 1L } };
        // Act / Assert
        (await RowsAsync(session, "MERGE (n:N {k:[x IN $l | x*2][0]}) RETURN n.k", parameters))[0][0].ShouldBe(2L);
        var rows = await RowsAsync(session, "UNWIND [[1],[2],[1]] AS l MERGE (n:N {k:[x IN l | x*2][0]}) RETURN count(*)");
        rows[0][0].ShouldBe(3L);
        (await RowsAsync(session, "MATCH (n:N) RETURN n.k ORDER BY n.k")).Select(row => row[0]).ShouldBe(new object?[] { 2L, 4L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE: both SET branches evaluate indexed comprehensions per row")]
    public async Task ExecuteAsync_MergeActions_ShouldEvaluateSelectedBranchAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("collection-actions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        await session.ExecuteAsync("UNWIND [1,2,1] AS k MERGE (n:N {k:k}) ON CREATE SET n.c=[x IN [k] | x+10][0] ON MATCH SET n.m=[x IN [k] | x+20][0]", cancellationToken: CancellationToken.None);
        // Assert
        var rows = await RowsAsync(session, "MATCH (n:N) RETURN n.k,n.c,n.m ORDER BY n.k");
        rows.Length.ShouldBe(2);
        rows[0].ShouldBe(new object?[] { 1L, 11L, 21L });
        rows[1].ShouldBe(new object?[] { 2L, 12L, null });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Property expressions: parse complete scalar results before admission")]
    [InlineData("INSERT (n:N {k:[x IN [1] | x+10][0]}) RETURN n.k")]
    [InlineData("MATCH (n:N) SET n.k=[x IN [n.k] | x+10][0] RETURN n.k")]
    [InlineData("MATCH (n:N) SET n={k:[x IN [n.k] | x+10][0]} RETURN n.k")]
    [InlineData("MATCH (n:N) SET n += {k:[x IN [n.k] | x+10][0]} RETURN n.k")]
    [InlineData("MATCH (n:N {k:[x IN [1] | x][0]}) RETURN n.k+10")]
    [InlineData("MATCH (n:N) SET n.k=[11,12][0..1][0] RETURN n.k")]
    [InlineData("MATCH (n:N) SET n.k={k:11}.k RETURN n.k")]
    public async Task ExecuteAsync_ScalarProperty_ShouldAcceptCompletedExpressionAsync(string query)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("collection-properties", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1})", cancellationToken: CancellationToken.None);
        // Act / Assert
        (await RowsAsync(session, query))[0][0].ShouldBe(11L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE: statically list-valued comprehensions retain the property capability refusal")]
    [InlineData("MERGE (n:N {k:([x IN [1] | x])}) RETURN n.k")]
    [InlineData("MATCH (n:N) SET n.k=([x IN [1] | x]) RETURN n.k")]
    [InlineData("INSERT (n:N {k:([1,2][0..1])}) RETURN n.k")]
    public async Task ExecuteAsync_CompositeProperty_ShouldRefuseCapabilityAsync(string query)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("collection-composites", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Property results: dynamic containers retain ordinary and MERGE constraint codes")]
    [InlineData("INSERT (n:N {k:[x IN $l | x][0]})", "CG03003")]
    [InlineData("MATCH (n:N) SET n.k=[x IN $l | x][0]", "CG03003")]
    [InlineData("MATCH (n:N) SET n={k:[x IN $l | x][0]}", "CG03003")]
    [InlineData("MATCH (n:N) SET n += {k:[x IN $l | x][0]}", "CG03003")]
    [InlineData("MERGE (n:N {k:[x IN $l | x][0]})", "CG03004")]
    public async Task ExecuteAsync_DynamicProperty_ShouldRetainConstraintDiagnosticAsync(string query, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("collection-dynamic", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1})", cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["l"] = new object?[] { new object?[] { 1L } } };
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, parameters, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        (await RowsAsync(session, "MATCH (n:N) RETURN n.k"))[0][0].ShouldBe(1L);
    }

    private static async Task<object?[][]> RowsAsync(GraphDatabaseSession session, string query, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, parameters, CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[result.Columns.Count];
            for (int i = 0; i < values.Length; i++) { values[i] = row.GetValue(i); }
            rows.Add(values);
        }
        return rows.ToArray();
    }
}
