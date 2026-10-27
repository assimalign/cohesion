using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphOrdinaryWriteAllocationTests
{
    private readonly ITestOutputHelper _output;

    public GraphOrdinaryWriteAllocationTests(ITestOutputHelper output) { _output = output; }

#if !DEBUG
    [Fact(DisplayName = "Cohesion Test [Graph] - ordinary CREATE SET DELETE retain pre-MERGE-fix allocation minima (#1735)")]
    public async Task Execute_OrdinaryWrites_ShouldRetainBaseAllocations()
    {
        // Arrange: reuse the same small graph; each CREATE is followed by its DELETE.
        await using var engine = GraphDatabaseEngine.Create("allocation", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE (:Counter {c:0})", cancellationToken: CancellationToken.None);
        string[] queries = ["CREATE (:Temporary {id:1})", "MATCH (n:Counter) SET n.c=n.c+1", "MATCH (n:Temporary) DELETE n"];
        var minima = new[] { long.MaxValue, long.MaxValue, long.MaxValue };
        for (int warmup = 0; warmup < 100; warmup++) { Round(false); }

        // Act: minimum across five rounds of 100 complete statements of each kind.
        for (int round = 0; round < 5; round++) { Round(true); }

        // Assert: the baseline values are measured on 7c2d19e35 in this same fixture.
        _output.WriteLine($"ordinary writes bytes/statement: CREATE={minima[0]}; SET={minima[1]}; DELETE={minima[2]}");
        minima[0].ShouldBeLessThanOrEqualTo(38_952);
        minima[1].ShouldBeLessThanOrEqualTo(67_120);
        minima[2].ShouldBeLessThanOrEqualTo(50_296);

        void Round(bool measure)
        {
            for (int i = 0; i < (measure ? 100 : 1); i++)
            {
                for (int kind = 0; kind < queries.Length; kind++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    var pending = session.ExecuteAsync(queries[kind], cancellationToken: CancellationToken.None);
                    pending.IsCompletedSuccessfully.ShouldBeTrue();
                    var result = pending.GetAwaiter().GetResult();
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    result.AffectedCount.ShouldBe(1);
                    if (measure) { minima[kind] = Math.Min(minima[kind], bytes); }
                }
            }
        }
    }
#endif
}
