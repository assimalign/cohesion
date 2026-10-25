using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

[Collection(TimingCollection.Name)]
public sealed class BTreePruneTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Prune: deleters at the horizon and live entries remain (#1195)")]
    public async Task PruneAsync_HorizonEqualsDeleter_ShouldKeepItUntilHorizonAdvances()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var (index, writer, deleter) = await SeedAsync(harness, 3, liveLast: true);

        // Act / Assert
        (await harness.IndexManager.PruneAsync(deleter)).ShouldBe(0);
        Count(harness).ShouldBe(3);
        (await harness.IndexManager.PruneAsync(new TransactionSequence(deleter.Value + 1))).ShouldBe(2);
        Count(harness).ShouldBe(1);
        var reader = await harness.BeginAsync();
        await using var cursor = index.OpenCursor(reader, IndexKeyRange.All);
        (await cursor.MoveNextAsync()).ShouldBeTrue();
        cursor.CurrentEntryReference.ShouldBe(3UL);
        (await cursor.MoveNextAsync()).ShouldBeFalse();
        await harness.RollbackAsync(reader);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Prune: cancellation rolls back the current page and retries it (#1195)")]
    public async Task PruneAsync_CancelledInsidePage_ShouldRestoreThatPageAndResumeThere()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var (_, _, deleter) = await SeedAsync(harness, 1_000);
        var bound = new TransactionSequence(deleter.Value + 1);
        using var cancelled = new CancellationTokenSource();
        int reads = 0;

        // Act
        OperationCanceledException? failure = null;
        try
        {
            BTreeIndex.EntryAccessed = () => { if (++reads == 20) { cancelled.Cancel(); } };
            try { await harness.IndexManager.PruneAsync(bound, cancelled.Token); }
            catch (OperationCanceledException exception) { failure = exception; }
        }
        finally { BTreeIndex.EntryAccessed = null; }

        // Assert: no partial page was committed and its continuation was not advanced.
        failure.ShouldNotBeNull().CancellationToken.ShouldBe(cancelled.Token);
        Count(harness).ShouldBe(1_000);
        (await harness.IndexManager.PruneAsync(bound)).ShouldBe(1_000);
        Count(harness).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Prune: a pass resumes after its page budget without reclaiming empty leaves (#1195)")]
    public async Task PruneAsync_MoreLeavesThanBudget_ShouldResumeAndKeepTreeShape()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var (_, _, deleter) = await SeedAsync(harness, 20_000);
        long root = harness.IndexManager.ExportRegistrations().Single().RootPageId;
        var before = BTreeShape.Read(harness.Storage, root);
        before.LeafCounts.Count.ShouldBeGreaterThan(BTreeIndexManager.PrunePageBudget);
        var bound = new TransactionSequence(deleter.Value + 1);

        // Act
        long first = await harness.IndexManager.PruneAsync(bound);
        long second = await harness.IndexManager.PruneAsync(bound);

        // Assert
        first.ShouldBeGreaterThan(0);
        first.ShouldBeLessThan(20_000);
        (first + second).ShouldBe(20_000);
        var after = BTreeShape.Read(harness.Storage, root);
        after.LeafCounts.Sum().ShouldBe(0);
        after.LeafCounts.Count.ShouldBe(before.LeafCounts.Count);
        after.Height.ShouldBe(before.Height);
        (await harness.IndexManager.PruneAsync(bound)).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Prune: the shared page budget resumes across catalog drop and create (#1195)")]
    public async Task PruneAsync_ManyIndexesAndCatalogChange_ShouldVisitEveryRemainingTree()
    {
        // Arrange: each tree occupies one leaf, so a pass must stop before the catalog ends.
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var indexes = new BTreeIndex[100];
        for (int i = 0; i < indexes.Length; i++)
        {
            indexes[i] = await harness.IndexManager.CreateIndexAsync(setup, (ulong)i + 1, new IndexDefinition("prune"));
        }
        await harness.CommitAsync(setup);
        var writer = await harness.BeginAsync();
        await harness.CommitAsync(writer);
        var deleter = await harness.BeginAsync();
        await harness.CommitAsync(deleter);
        using (var build = harness.Storage.BeginTransaction())
        {
            foreach (var index in indexes)
            {
                await index.InsertVersionAsync(build, IndexKey.FromInt64(1), 1, writer.Sequence, deleter.Sequence);
            }
            build.Commit();
        }
        var bound = new TransactionSequence(deleter.Sequence.Value + 1);

        // Act: remove the next scheduled tree (swapping the last into its directory slot),
        // then append a replacement. Both operations must preserve the remaining work.
        (await harness.IndexManager.PruneAsync(bound)).ShouldBe(BTreeIndexManager.PrunePageBudget);
        var change = await harness.BeginAsync();
        await harness.IndexManager.DropIndexAsync(change, 65, "prune");
        var replacement = await harness.IndexManager.CreateIndexAsync(change, 101, new IndexDefinition("prune"));
        await replacement.InsertVersionAsync(harness.GetStorageTransaction(change), IndexKey.FromInt64(1), 1, writer.Sequence, deleter.Sequence);
        await harness.CommitAsync(change);
        long second = await harness.IndexManager.PruneAsync(bound);

        // Assert: 36 unvisited entries remain after the first pass, including the replacement.
        second.ShouldBe(100 - BTreeIndexManager.PrunePageBudget);
        foreach (var registration in harness.IndexManager.ExportRegistrations())
        {
            BTreeShape.Read(harness.Storage, registration.RootPageId).LeafCounts.Sum().ShouldBe(0);
        }
        (await harness.IndexManager.PruneAsync(bound)).ShouldBe(0);
    }

    private static async Task<(BTreeIndex Index, TransactionSequence Writer, TransactionSequence Deleter)> SeedAsync(
        IndexTestHarness harness, int count, bool liveLast = false)
    {
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("prune"));
        await harness.CommitAsync(setup);
        var writer = await harness.BeginAsync();
        await harness.CommitAsync(writer);
        var deleter = await harness.BeginAsync();
        await harness.CommitAsync(deleter);
        using var build = harness.Storage.BeginTransaction();
        for (int value = 1; value <= count; value++)
        {
            await index.InsertVersionAsync(build, IndexKey.FromInt64(value), (ulong)value,
                writer.Sequence, liveLast && value == count ? TransactionSequence.None : deleter.Sequence);
        }
        build.Commit();
        return (index, writer.Sequence, deleter.Sequence);
    }

    private static int Count(IndexTestHarness harness)
        => BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId).LeafCounts.Sum();
}
