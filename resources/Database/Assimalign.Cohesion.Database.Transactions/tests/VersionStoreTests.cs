using System;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Tests for the in-memory version store: chain resolution against snapshots,
/// pruning below the oldest-active bound, and aborted-writer purging (#850).
/// </summary>
public class VersionStoreTests
{
    private static byte[] Payload(string text) => Encoding.UTF8.GetBytes(text);

    private static TransactionSnapshot SnapshotFor(ulong owner, ulong minimum, ulong maximum, params ulong[] active)
    {
        var sequences = new TransactionSequence[active.Length];
        for (int i = 0; i < active.Length; i++)
        {
            sequences[i] = new TransactionSequence(active[i]);
        }

        return new TransactionSnapshot(
            new TransactionSequence(owner), new TransactionSequence(minimum), new TransactionSequence(maximum), sequences);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Versions: newest visible version wins")]
    public async Task GetVisibleVersion_ChainOfVersions_ShouldResolveNewestVisible()
    {
        // Arrange: versions by writers 2, 4, 6; the snapshot sees through 5 with 4 active.
        var store = VersionStore.CreateInMemory();
        await store.AppendVersionAsync(1, 1, Payload("v2"), new TransactionSequence(2));
        await store.AppendVersionAsync(1, 1, Payload("v4"), new TransactionSequence(4));
        await store.AppendVersionAsync(1, 1, Payload("v6"), new TransactionSequence(6));

        var snapshot = SnapshotFor(owner: 5, minimum: 4, maximum: 6, active: 4);

        // Act
        var visible = await store.GetVisibleVersionAsync(1, 1, snapshot);

        // Assert: 6 is above maximum, 4 is active — 2 wins.
        Encoding.UTF8.GetString(visible!.Value.Span).ShouldBe("v2");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Versions: prune keeps the newest universally visible version")]
    public async Task Prune_OldVersions_ShouldKeepNewestBelowOldestActive()
    {
        // Arrange: writers 1, 2, 3 with oldest-active 3 — versions 1 and 2 are both
        // below the bound; only the NEWEST of them (2) must survive.
        var store = VersionStore.CreateInMemory();
        await store.AppendVersionAsync(1, 1, Payload("v1"), new TransactionSequence(1));
        await store.AppendVersionAsync(1, 1, Payload("v2"), new TransactionSequence(2));
        await store.AppendVersionAsync(1, 1, Payload("v3"), new TransactionSequence(3));

        // Act
        long pruned = await store.PruneAsync(new TransactionSequence(3));

        // Assert
        pruned.ShouldBe(1);
        var oldSnapshot = SnapshotFor(owner: 100, minimum: 3, maximum: 101, active: 3);
        Encoding.UTF8.GetString((await store.GetVisibleVersionAsync(1, 1, oldSnapshot))!.Value.Span).ShouldBe("v2");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Versions: purging a writer removes its versions everywhere")]
    public async Task PurgeWriter_MultipleChains_ShouldRemoveAllVersions()
    {
        // Arrange
        var store = VersionStore.CreateInMemory();
        await store.AppendVersionAsync(1, 1, Payload("keep"), new TransactionSequence(1));
        await store.AppendVersionAsync(1, 1, Payload("drop"), new TransactionSequence(9));
        await store.AppendVersionAsync(2, 5, Payload("drop-too"), new TransactionSequence(9));

        // Act
        long removed = await store.PurgeWriterAsync(new TransactionSequence(9));

        // Assert
        removed.ShouldBe(2);
        var everything = SnapshotFor(owner: 100, minimum: 100, maximum: 101);
        Encoding.UTF8.GetString((await store.GetVisibleVersionAsync(1, 1, everything))!.Value.Span).ShouldBe("keep");
        (await store.GetVisibleVersionAsync(2, 5, everything)).ShouldBeNull();
    }
}
