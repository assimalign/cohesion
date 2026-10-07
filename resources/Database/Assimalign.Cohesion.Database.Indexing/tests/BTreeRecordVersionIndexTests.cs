using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// <see cref="BTreeRecordVersionIndex"/> is built through its static factory only (rule 1, owner
/// decision 27 of 2026-10-06), and the binding it returns undoes through the B-tree it was given:
/// the record-space ledger's erase and clear-deleter reach exactly the version they name.
/// </summary>
public sealed class BTreeRecordVersionIndexTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Record version index: the factory is the only way in and refuses a null index")]
    public void Create_NullIndex_ShouldThrowAndExposeNoPublicConstructor()
    {
        // Act / Assert
        typeof(BTreeRecordVersionIndex).GetConstructors().ShouldBeEmpty();
        Should.Throw<ArgumentNullException>(() => BTreeRecordVersionIndex.Create(null!)).ParamName.ShouldBe("index");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Record version index: erase and clear-deleter reach the version they name through the bound tree")]
    public async Task Create_BoundIndex_ShouldEraseAndClearDeleterThroughTheTree()
    {
        // Arrange: two versions of one entry, the first deleted by the second's writer.
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, objectId: 1, new IndexDefinition("ix_versions", IndexKind.BTree, false));
        await harness.CommitAsync(setup);

        var first = await CommittedSequenceAsync(harness);
        var second = await CommittedSequenceAsync(harness);
        var key = IndexKey.FromInt64(42);
        const ulong reference = 7UL;
        using (var build = harness.Storage.BeginTransaction())
        {
            await index.InsertVersionAsync(build, key, reference, first, second);
            await index.InsertVersionAsync(build, key, reference, second, TransactionSequence.None);
            build.Commit();
        }

        var versions = BTreeRecordVersionIndex.Create(index);
        (await VisibleCountAsync(harness, index, key)).ShouldBe(1);

        // Act / Assert: clearing the second writer's deletion revives the first version.
        using (var undo = harness.Storage.BeginTransaction())
        {
            await versions.ClearDeleterAsync(undo, key.Encoded, reference, second);
            undo.Commit();
        }
        (await VisibleCountAsync(harness, index, key)).ShouldBe(2);

        // Erasing the second writer's version leaves the first alone.
        using (var undo = harness.Storage.BeginTransaction())
        {
            await versions.EraseAsync(undo, key.Encoded, reference, second);
            undo.Commit();
        }
        (await VisibleCountAsync(harness, index, key)).ShouldBe(1);
    }

    private static async Task<TransactionSequence> CommittedSequenceAsync(IndexTestHarness harness)
    {
        var context = await harness.BeginAsync();
        await harness.CommitAsync(context);
        return context.Sequence;
    }

    private static async Task<int> VisibleCountAsync(IndexTestHarness harness, BTreeIndex index, IndexKey key)
    {
        var reader = await harness.BeginAsync();
        int count = 0;
        await using (var cursor = index.OpenCursor(reader, new IndexKeyRange(key, key, IsStartInclusive: true, IsEndInclusive: true), false))
        {
            while (await cursor.MoveNextAsync())
            {
                count++;
            }
        }

        await harness.RollbackAsync(reader);
        return count;
    }
}
