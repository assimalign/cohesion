using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

public sealed class BTreeRangeCountTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Range count: bounds, duplicate keys and physical entry limits are exact (#1522)")]
    public async Task CountRange_BoundsAndLimits_ShouldCountVisibleEntries()
    {
        await using var harness = new IndexTestHarness();
        var seed = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(seed, 1, new IndexDefinition("ix_count"));
        for (int i = 0; i < 600; i++) { await index.InsertAsync(seed, IndexKey.FromInt64(i / 2), (ulong)i); }
        await harness.CommitAsync(seed);
        var reader = await harness.Manager.BeginAsync();
        var range = new IndexKeyRange(IndexKey.FromInt64(10), IndexKey.FromInt64(20), false, true);
        var exact = index.CountRange(reader.Snapshot, range);
        exact.IsComplete.ShouldBeTrue();
        exact.Count.ShouldBe(20);
        exact.EntriesExamined.ShouldBe(21); // the stopping key also consumes physical work
        var limited = index.CountRange(reader.Snapshot, IndexKeyRange.All, entryLimit: 17);
        limited.ShouldBe(new BTreeRangeCount(17, false, 17, limited.PagesVisited));
        limited.PagesVisited.ShouldBeInRange(1, 32);
        var pageBound = index.CountRange(reader.Snapshot, IndexKeyRange.All, pageLimit: 1);
        pageBound.IsComplete.ShouldBeFalse();
        pageBound.PagesVisited.ShouldBe(1);
        await harness.Manager.CommitAsync(reader);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Range count: invisible versions consume the physical work budget (#1522)")]
    public async Task CountRange_InvisibleVersions_ShouldBoundPhysicalWork()
    {
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_count"));
        await harness.CommitAsync(setup);
        var writer = await harness.BeginAsync();
        for (int i = 0; i < 100; i++) { await index.InsertAsync(writer, IndexKey.FromInt64(i), (ulong)i); }
        var reader = await harness.Manager.BeginAsync();
        var count = index.CountRange(reader.Snapshot, IndexKeyRange.All, entryLimit: 11);
        count.Count.ShouldBe(0);
        count.EntriesExamined.ShouldBe(11);
        count.IsComplete.ShouldBeFalse();
        await harness.Manager.CommitAsync(reader);
        await harness.RollbackAsync(writer);
        var clean = await harness.Manager.BeginAsync();
        index.CountRange(clean.Snapshot, IndexKeyRange.All).Count.ShouldBe(0);
        await harness.Manager.CommitAsync(clean);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Range count: invalid limits and cancellation release ownership (#1522)")]
    public async Task CountRange_InvalidLimitsAndCancellation_ShouldPreserveTree()
    {
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_count"));
        await harness.CommitAsync(setup);
        var reader = await harness.Manager.BeginAsync();
        Should.Throw<ArgumentOutOfRangeException>(() => index.CountRange(reader.Snapshot, IndexKeyRange.All, entryLimit: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => index.CountRange(reader.Snapshot, IndexKeyRange.All, pageLimit: 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Should.Throw<OperationCanceledException>(() => index.CountRange(reader.Snapshot, IndexKeyRange.All, cancellationToken: cancellation.Token))
            .CancellationToken.ShouldBe(cancellation.Token);
        index.CountRange(reader.Snapshot, IndexKeyRange.All).IsComplete.ShouldBeTrue();
        await harness.Manager.CommitAsync(reader);
    }
}
