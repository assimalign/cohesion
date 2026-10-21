using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Client.Internal;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

[Collection(nameof(GraphClientEventSourceCollection))]
public sealed class GraphPathAllocationTests
{
    private readonly ITestOutputHelper _output;
    public GraphPathAllocationTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Twenty TCP paths retain their digest within the allocation budget")]
    public async Task QueryPathsAsync_TwentyTcpPaths_ShouldBoundAllocationsAndRetainDigest()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: true);
        await using (var session = await harness.Database.CreateSessionAsync(harness.Token))
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: harness.Token);
            var person = await harness.Database.CreateNodeAsync(session, ["Person"],
                new Dictionary<string, object?> { ["name"] = "Alice", ["bio"] = new string('a', 512) }, harness.Token);
            for (int i = 0; i < 20; i++)
            {
                var message = await harness.Database.CreateNodeAsync(session, ["Message"],
                    new Dictionary<string, object?> { ["index"] = i, ["text"] = new string('x', 512) }, harness.Token);
                await harness.Database.CreateRelationshipAsync(session, person.Id, message.Id, "POSTED",
                    new Dictionary<string, object?> { ["ordinal"] = i }, harness.Token);
            }
            await transaction.CommitAsync(harness.Token);
        }
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        async Task ReadAsync()
        {
            int count = 0;
            long digest = 0;
            long decodes = GraphClientEventSource.Log.PathDecodeCalls;
            await foreach (var path in connection.QueryPathsAsync(
                "MATCH p = (a:Person)-[:POSTED]->(b:Message) RETURN p ORDER BY b.index LIMIT 20", cancellationToken: harness.Token))
            {
                count++;
                digest += Convert.ToInt64(path.Nodes[1].Properties["index"]);
                path.Nodes[0].Properties["name"].ShouldBe("Alice");
            }
            count.ShouldBe(20);
            digest.ShouldBe(190);
            (GraphClientEventSource.Log.PathDecodeCalls - decodes).ShouldBe(20);
        }
        for (int i = 0; i < 10; i++) { await ReadAsync(); }

        // Act: whole-process bytes include both ends of TCP; minimum of three rounds.
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < 30; i++) { await ReadAsync(); }
            long bytes = (GC.GetTotalAllocatedBytes(precise: true) - before) / 30;
            minimum = Math.Min(minimum, bytes);
            _output.WriteLine($"Round {round}: {bytes} bytes/op");
        }

        // Assert
        _output.WriteLine($"Minimum: {minimum} bytes/op; digest=190, paths=20");
        // Before #1539 this exact TCP fixture measured 644,499 bytes/op (minimum of three).
        // A byte budget is deterministic across host speeds and pins the required 40% reduction.
        minimum.ShouldBeLessThanOrEqualTo(386_699L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Backpressured paths release rentals during cancellation and connection disposal")]
    public async Task QueryPathsAsync_BackpressuredDisposal_ShouldJoinProducerAndPreserveOwnedResults()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: true);
        await using (var seed = await harness.Client.ConnectAsync(harness.Token))
        {
            await seed.ExecuteAsync("INSERT (:Person {name: 'Alice'}), (:Person {name: 'Bob'}), (:Person {name: 'Carol'})",
                cancellationToken: harness.Token);
        }
        var clock = Stopwatch.StartNew();
        int races = 0;
        long waits = GraphClientEventSource.Log.PathBackpressureWaits;

        // Act: wait for the producer's queue-full counter before racing lease disposal with it.
        do
        {
            await using var connection = await harness.Client.ConnectAsync(harness.Token);
            await using var paths = connection.QueryPathsAsync("MATCH (n:Person) RETURN n ORDER BY n.name",
                cancellationToken: harness.Token).GetAsyncEnumerator();
            (await paths.MoveNextAsync()).ShouldBeTrue();
            var retained = paths.Current;
            // The producer can already be waiting; require a wait in this exchange, not a delay.
            while (GraphClientEventSource.Log.PathBackpressureWaits == waits)
            {
                await Task.Delay(1, harness.Token);
            }
            await connection.DisposeAsync();
            await paths.DisposeAsync();
            retained.Nodes[0].Properties["name"].ShouldBe("Alice");
            races++;
            waits = GraphClientEventSource.Log.PathBackpressureWaits;
            harness.Token.ThrowIfCancellationRequested();
        }
        while (clock.Elapsed < TimeSpan.FromMilliseconds(100) || races < 8);

        // Assert: every iteration exercised a blocked producer and returned its only pool slot.
        races.ShouldBeGreaterThanOrEqualTo(8);
        await using var final = await harness.Client.ConnectAsync(harness.Token);
        (await final.QueryAsync("MATCH (n:Person) RETURN n.name", cancellationToken: harness.Token)).Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Backpressured and idle pooled connections retain no borrowed payload rental")]
    public async Task QueryPathsAsync_BackpressuredAndIdle_ShouldReleaseDecodedPayloads()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: true);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:Person {name: 'Alice'}), (:Person {name: 'Bob'}), (:Person {name: 'Carol'})",
            cancellationToken: harness.Token);
        var protocolSource = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Protocol");
        using var recorder = new Assimalign.Cohesion.Testing.EventSourceRecorder(protocolSource,
            EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        var baseline = await recorder.WaitForCounterAsync("pooled-buffers-in-use", 0, harness.Token);
        long waits = GraphClientEventSource.Log.PathBackpressureWaits;
        long decodes = GraphClientEventSource.Log.PathDecodeCalls;
        await using var paths = connection.QueryPathsAsync("MATCH (n:Person) RETURN n ORDER BY n.name",
            cancellationToken: harness.Token).GetAsyncEnumerator();

        // Act: retain the first path while the next occupies the sole handoff slot.
        (await paths.MoveNextAsync()).ShouldBeTrue();
        var retained = paths.Current;
        while (GraphClientEventSource.Log.PathDecodeCalls - decodes < 2
            || GraphClientEventSource.Log.PathBackpressureWaits == waits)
        {
            await Task.Delay(1, harness.Token);
        }
        var backpressured = await recorder.WaitForCounterAsync("pooled-buffers-in-use", recorder.CounterSequence, harness.Token);
        while (await paths.MoveNextAsync()) { }
        await connection.DisposeAsync();
        var idle = await recorder.WaitForCounterAsync("pooled-buffers-in-use", recorder.CounterSequence, harness.Token);

        // Assert: the baseline includes the server's session batch, but no client payload rental.
        backpressured.Value.ShouldBe(baseline.Value);
        idle.Value.ShouldBe(baseline.Value);
        (GraphClientEventSource.Log.PathDecodeCalls - decodes).ShouldBe(3);
        GraphClientEventSource.Log.PathBackpressureWaits.ShouldBeGreaterThan(waits);
        retained.Nodes[0].Properties["name"].ShouldBe("Alice");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Path decode and backpressure counters publish")]
    public async Task Counters_Enabled_ShouldPublishDecodeAndBackpressureCounters()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var recorder = new Assimalign.Cohesion.Testing.EventSourceRecorder(GraphClientEventSource.Log,
            EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act
        await recorder.WaitForCountersAsync(["path-decode-calls", "path-decode-milliseconds", "path-backpressure-waits"], timeout.Token);

        // Assert
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }
}
