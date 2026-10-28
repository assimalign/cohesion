using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphOrderAliasKindTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - ORDER BY alias kinds retain the incoming scope under iterator shadowing")]
    [InlineData("TRUE", "all(x IN [n] WHERE old)")]
    [InlineData("[TRUE]", "all(x IN [n] WHERE all(y IN old WHERE y))")]
    public async Task ExecuteAsync_ShadowedProjectionInput_ShouldResolveAliasKindAsync(string seed, string ordering)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("alias-kinds", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N)", cancellationToken: CancellationToken.None);
        string query = $"LET x={seed} MATCH (n:N) RETURN x AS old ORDER BY {ordering}";
        // Act
        await using var plan = (DatabasePlanDescription)await session.ExecuteAsync("EXPLAIN " + query, cancellationToken: CancellationToken.None);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        // Assert
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { count++; row.GetValue(0).ShouldBe(seed == "TRUE" ? true : new object?[] { true }); }
        count.ShouldBe(1);
    }

}
