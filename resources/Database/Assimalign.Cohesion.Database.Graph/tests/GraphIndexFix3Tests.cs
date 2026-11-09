using System;
using System.Collections.Generic;
using System.Threading;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphIndexFix3Tests
{
    private readonly ITestOutputHelper _output;
    public GraphIndexFix3Tests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Graph] - Oversized exact keys refuse before encoding (#1808)")]
    public void Key_Fix3OversizedString_ShouldRefuseWithoutMaterializing()
    {
        // Arrange: storage bounds nodes, but the codec also accepts caller values.
        string text = new('x', 1024 * 1024);
        string oversizedControl = new('x', 400);
        for (int i = 0; i < 8; i++)
        {
            GraphRecordCodec.Key("warm");
            // Warm resource lookup and assertion infrastructure in an isolated run.
            Should.Throw<GraphElementTooLargeException>(() => GraphRecordCodec.Key(oversizedControl));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();

        // Act.
        Should.Throw<GraphElementTooLargeException>(() => GraphRecordCodec.Key(text));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert.
        _output.WriteLine($"GRAPH_KEY|allocated={allocated}");
        allocated.ShouldBeLessThan(16 * 1024);
        GC.KeepAlive(text);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Text keys reserve before yielding their fixed-size encoding (#1808)")]
    public void Keys_Fix3TextString_ShouldChargeBeforeYielding()
    {
        // Arrange.
        long charge = 0;
        int count = 0;
        // Act / Assert: trigram extraction walks UTF-16 directly and never copies the string.
        foreach (var key in GraphTextTrigrams.Keys("abcdef", CancellationToken.None, bytes => charge += bytes))
        {
            count++;
            charge.ShouldBeGreaterThanOrEqualTo(128 + count * 256);
            key.Length.ShouldBe(9);
        }
        count.ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Exact numeric extraction reserves every temporary encoding buffer (#1808)")]
    public void PropertyKeys_Fix3NumericWorkspace_ShouldCoverAllocatedBuffers()
    {
        // Arrange: resolve the private generator before measuring its execution.
        for (int i = 0; i < 16; i++) { GraphRecordCodec.Key(1L); }
        long charge = 0;
        Action<long> reserve = bytes => charge += bytes;
        var factory = typeof(GraphStore).GetMethod("PropertyKeys", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        for (int i = 0; i < 8; i++)
        {
            var warm = (IEnumerable<IndexKey>)factory.Invoke(null, [new GraphRecord(3, 1000), 1L, 1UL, CancellationToken.None, reserve, null])!;
            foreach (var key in warm) { GC.KeepAlive(key); }
        }
        charge = 0;
        var sequence = (IEnumerable<IndexKey>)factory.Invoke(null, [new GraphRecord(3, 1000), 1L, 1UL, CancellationToken.None, reserve, null])!;
        using var iterator = sequence.GetEnumerator();
        long before = GC.GetAllocatedBytesForCurrentThread();
        // Act.
        bool found = iterator.MoveNext();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert.
        found.ShouldBeTrue();
        iterator.Current.Length.ShouldBe(17);
        allocated.ShouldBeGreaterThan(0);
        charge.ShouldBeGreaterThanOrEqualTo(allocated);
        _output.WriteLine($"GRAPH_NUMERIC_WORKSPACE|charge={charge}|allocated={allocated}");
    }
}
