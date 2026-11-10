using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphIndexWriteBudgetTests
{
    private readonly ITestOutputHelper _output;
    public GraphIndexWriteBudgetTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Graph] - Unrelated text indexes preserve exact-index write memory charges (#1808)")]
    public async Task Execute_Fix4ExactWrites_ShouldKeepMemoryCharges()
    {
        // Arrange / Act.
        var without = await MeasureAsync(false);
        var with = await MeasureAsync(true);
        // Assert: each CREATE, the bulk SET and the bulk DELETE have the same charge.
        _output.WriteLine($"WRITE_CHARGES|without={string.Join(',', without)}|with={string.Join(',', with)}");
        with.ShouldBe(without);
        // A 200-character exact key needs 5,080 bytes of build workspace. Even an
        // unconditional ordinary-write charge must trip this guard.
        for (int i = 0; i < 32; i++)
        {
            without[i].ShouldBeLessThan(5_080);
            with[i].ShouldBeLessThan(5_080);
        }
    }

    private static async Task<long[]> MeasureAsync(bool text)
    {
        await using var engine = GraphDatabaseEngine.Create("write-budget", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX p FOR (n:Person) ON (n.name)", cancellationToken: CancellationToken.None);
        if (text) { await session.ExecuteAsync("CREATE TEXT INDEX t FOR (d:Doc) ON (d.body)", cancellationToken: CancellationToken.None); }
        var charges = new List<long>();
        for (int i = 0; i < 32; i++)
        {
            await session.ExecuteAsync("CREATE (:Person {name:'" + new string('x', 200) + "'})", cancellationToken: CancellationToken.None);
            charges.Add(session.LastStatementMetrics!.Memory.PeakRetainedBytes);
        }
        foreach (string query in new[] { "MATCH (n:Person) SET n.age=1", "MATCH (n:Person) DELETE n" })
        {
            await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
            charges.Add(session.LastStatementMetrics!.Memory.PeakRetainedBytes);
        }
        return charges.ToArray();
    }
}
