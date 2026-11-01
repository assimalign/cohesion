#if !DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphDatabaseEventSourceCollection))]
public sealed class GqlKnownPropertyAllocationTests
{
    private readonly ITestOutputHelper _output;
    public GqlKnownPropertyAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Known entity property reads retain pre-warning allocation costs (#1249)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_KnownProperties_ShouldRetainReadAllocationAsync(bool several)
    {
        // Arrange: exclude parsing, seeding, warmup, and reporting from measurement.
        await using var engine = GraphDatabaseEngine.Create("read-allocations", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Keep {a:1,b:2,c:3})", cancellationToken: CancellationToken.None);
        var request = GraphQueryRequest.FromGql(several
            ? "MATCH (n:Keep) WHERE n.a=1 RETURN n.a,n.b,n.c,n.a AS again ORDER BY n.b,n.c"
            : "MATCH (n:Keep) RETURN n.a");
        void Execute()
        {
            var pending = session.ExecuteAsync(request, CancellationToken.None);
            if (!pending.IsCompleted) { pending.IsCompleted.ShouldBeTrue(); }
            var result = (DatabaseResultSet)pending.GetAwaiter().GetResult();
            try
            {
                var rows = result.GetRowsAsync(CancellationToken.None).GetAsyncEnumerator();
                try
                {
                    var move = rows.MoveNextAsync();
                    if (!move.IsCompleted || !move.GetAwaiter().GetResult()) { throw new InvalidOperationException("Expected one synchronous row."); }
                    if (!Equals(rows.Current.GetValue(0), 1L)) { rows.Current.GetValue(0).ShouldBe(1L); }
                    move = rows.MoveNextAsync();
                    if (!move.IsCompleted || move.GetAwaiter().GetResult()) { throw new InvalidOperationException("Expected only one synchronous row."); }
                }
                finally { var cleanup = rows.DisposeAsync(); cleanup.IsCompleted.ShouldBeTrue(); cleanup.GetAwaiter().GetResult(); }
            }
            finally { var cleanup = result.DisposeAsync(); cleanup.IsCompleted.ShouldBeTrue(); cleanup.GetAwaiter().GetResult(); }
        }
        for (int i = 0; i < 200; i++) { Execute(); }

        // Act: minimum of three independent warmed rounds, 1,000 queries each.
        double minimum = double.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++) { Execute(); }
            double bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 1_000d;
            minimum = Math.Min(minimum, bytes);
            _output.WriteLine($"{(several ? "several" : "one")} round {round + 1}: {bytes:F3} B/query");
        }

        // Assert: no more than the fresh three-round minima at 311cdf224 (d786a2c08 is identical).
        // The ceilings were measured on Windows ARM64, the highest of the CI platforms: Ubuntu x64,
        // macOS and Windows x64 measure a few bytes per query less, so an exact value fails there.
        _output.WriteLine($"{(several ? "several" : "one")} minimum: {minimum:F3} B/query");
        minimum.ShouldBeLessThanOrEqualTo(several ? 36_277.824 : 25_721.440);
    }
}
#endif
