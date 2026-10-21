using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphOrderKeyTests
{
    private readonly ITestOutputHelper _output;

    public GraphOrderKeyTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering: ten thousand 64-field map keys sort from cached values")]
    public void Apply_LargeMapKeys_ShouldOrderRows()
    {
        // Arrange
        var rows = new List<GraphOrderedRow>();
        for (int i = 0; i < 10_000; i++)
        {
            var map = new Dictionary<string, object?>();
            for (int field = 0; field < 64; field++) { map.Add($"field{field:000}", field == 63 ? (long)i : 0L); }
            rows.Add(new GraphOrderedRow([i], [GraphOrderComparer.Prepare(map)], i));
        }
        var stage = new GraphSortPagePlan([new(new GqlSortItem(new GqlLiteralExpression(0L)), -1)], 0, 10);
        // Warm the comparison and sorting paths at the measured size before measuring, so one-time
        // runtime work on this thread (tiering, first use of the full-size path) is not counted.
        GraphSortPage.Apply(rows.Take(32).ToList(), stage, CancellationToken.None);
        GraphSortPage.Apply(rows.ToList(), stage, CancellationToken.None);
        // Act: one round can still pick up one-time runtime allocations on this thread (tier-up,
        // OSR) under a loaded host, so the smallest of three rounds is the sort's own cost.
        IReadOnlyList<object?[]> result = [];
        long allocated = long.MaxValue;
        var watch = new Stopwatch();
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart();
            result = GraphSortPage.Apply(rows, stage, CancellationToken.None);
            watch.Stop();
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        // Assert
        result.Select(row => row[0]).ShouldBe(Enumerable.Range(0, 10).Cast<object?>());
        allocated.ShouldBeLessThan(4096);
        _output.WriteLine($"64-field map sort: {allocated:N0} bytes, {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering: nested map comparisons neither allocate nor revisit source keys")]
    public void Compare_NestedMaps_ShouldUsePreparedKeyOrder()
    {
        // Arrange
        var nested = new Dictionary<string, object?> { ["z"] = 0L, ["a"] = 1L };
        var source = new Dictionary<string, object?> { ["b"] = new object?[] { nested }, ["a"] = nested };
        object? left = GraphOrderComparer.Prepare(source), right = GraphOrderComparer.Prepare(source);
        nested.Clear();
        source.Clear();
        GraphOrderComparer.Compare(left, right).ShouldBe(0);
        // Warm the measured loop past tiered compilation's call-count threshold, as the mixed-numeric
        // test's oracle loop does, so one-time runtime work on this thread is not counted.
        for (int i = 0; i < 1000; i++) { GraphOrderComparer.Compare(left, right); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        // Act
        int order = 0;
        for (int i = 0; i < 1000; i++) { order |= GraphOrderComparer.Compare(left, right); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert
        order.ShouldBe(0);
        allocated.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering: mixed numeric comparisons remain exact without allocations")]
    public void Compare_MixedNumericMaps_ShouldMatchExactValueOrderWithoutAllocating()
    {
        // Arrange
        var values = new List<object> { long.MinValue, long.MaxValue, ulong.MaxValue, decimal.MinValue, decimal.MaxValue,
            double.MinValue, double.MaxValue, double.Epsilon, -double.Epsilon, float.Epsilon, -float.Epsilon,
            0.1m, 0.1d, 0L, -0.0d, 1, (short)-1, (sbyte)-1, (byte)1, (ushort)1, (uint)1,
            9007199254740993L, 9007199254740992d, 0.0000000000000000000000000001m };
        var random = new Random(1142);
        for (int i = 0; i < 32; i++)
        {
            double floating = BitConverter.Int64BitsToDouble(random.NextInt64());
            if (double.IsFinite(floating)) { values.Add(i % 2 == 0 ? floating : -floating); }
            values.Add(new decimal(random.Next(), random.Next(), random.Next(), i % 2 == 0, (byte)(i % 29)));
            values.Add(random.NextInt64(long.MinValue, long.MaxValue));
        }
        var keys = values.Select(value => GraphOrderComparer.Prepare(new Dictionary<string, object?> { ["v"] = value })).ToArray();
        // Act / Assert: the existing reduced-fraction comparer is the independent equality/order oracle.
        for (int i = 0; i < values.Count; i++)
        {
            for (int j = 0; j < values.Count; j++)
            { Math.Sign(GraphOrderComparer.Compare(keys[i], keys[j])).ShouldBe(Math.Sign(GraphValueComparer.CompareNumbers(values[i], values[j]))); }
        }
        // The smallest of three rounds excludes one-time runtime allocations on this thread.
        int same = 0;
        long allocated = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            same = 0;
            for (int i = 0; i < keys.Length; i++)
            { for (int j = 0; j < keys.Length; j++) { same += Math.Sign(GraphOrderComparer.Compare(keys[i], keys[j])); } }
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        same.ShouldBe(0);
        allocated.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering: path relationship sequence precedes length and family order includes relationships")]
    public void Apply_PathAndRelationshipKeys_ShouldCompareSequenceBeforeLength()
    {
        // Arrange
        var a = new GraphNode(new GraphNodeId(1), [], new Dictionary<string, object?>());
        var b = new GraphNode(new GraphNodeId(2), [], new Dictionary<string, object?>());
        var c = new GraphNode(new GraphNodeId(3), [], new Dictionary<string, object?>());
        var first = new GraphRelationship(new GraphRelationshipId(1), "R", a.Id, b.Id, new Dictionary<string, object?>());
        var second = new GraphRelationship(new GraphRelationshipId(2), "R", b.Id, c.Id, new Dictionary<string, object?>());
        var third = new GraphRelationship(new GraphRelationshipId(3), "R", a.Id, c.Id, new Dictionary<string, object?>());
        object?[] values = [new GraphPath([b, c], [second]), new GraphPath([a, c], [third]),
            new GraphPath([a, b, c], [first, second]), new GraphPath([a, b], [first]), Array.Empty<object?>(), third, first, b, a];
        var rows = values.Select((value, i) => new GraphOrderedRow([i], [value], i)).ToList();
        // Act
        var result = GraphSortPage.Apply(rows, new GraphSortPagePlan([new(new GqlSortItem(new GqlLiteralExpression(0L)), -1)], 0, null), CancellationToken.None);
        // Assert
        result.Select(row => row[0]).ShouldBe(new object?[] { 8, 7, 6, 5, 4, 3, 2, 1, 0 });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Ordering: cancellation during map sort unwraps and releases the statement")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_CancelDuringMapSort_ShouldPreserveCancellationStateAsync(bool explicitTransaction)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("cancel-sort", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        if (transaction is not null) { await session.ExecuteAsync("INSERT (:Undone)", cancellationToken: CancellationToken.None); }
        using var cancellation = new CancellationTokenSource();
        var keys = new CancelingReadOnlyList<GraphSortKeyPlan>([new(new GqlSortItem(new GqlLiteralExpression(0L)), -1)], cancellation, 128);
        var rows = Enumerable.Range(0, 10_000).Reverse().Select(i => new GraphOrderedRow([i],
            [GraphOrderComparer.Prepare(new Dictionary<string, object?> { ["key"] = (long)i })], i)).ToList();
        var watch = Stopwatch.StartNew();
        // Act
        var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
            await database.RunAsync(session, _ => new ValueTask<object?[][]>(GraphSortPage.Apply(rows,
                new GraphSortPagePlan(keys, 0, 10), cancellation.Token)), cancellation.Token));
        // Assert
        keys.Reads.ShouldBe(128);
        error.CancellationToken.ShouldBe(cancellation.Token);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            var refused = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("RETURN 1", cancellationToken: CancellationToken.None));
            refused.Diagnostics[0].Code.ShouldBe("COHDBG007");
            await transaction.RollbackAsync(CancellationToken.None);
        }
        await using var reused = (await session.ExecuteAsync("RETURN 1", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        reused.Status.ShouldBe(DatabaseResultStatus.Success);
        await using var undone = (await session.ExecuteAsync("MATCH (n:Undone) RETURN n", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        int remaining = 0;
        await foreach (var row in undone.GetRowsAsync(CancellationToken.None)) { remaining++; }
        remaining.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering: a pre-canceled map sort performs no comparisons")]
    public void Apply_CanceledToken_ShouldStopBeforeSorting()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var keys = new CancelingReadOnlyList<GraphSortKeyPlan>([new(new GqlSortItem(new GqlLiteralExpression(0L)), -1)], cancellation, 128);
        cancellation.Cancel();
        // Act
        Should.Throw<OperationCanceledException>(() => GraphSortPage.Apply([], new GraphSortPagePlan(keys, 0, 10), cancellation.Token));
        // Assert
        keys.Reads.ShouldBe(0);
    }
}
