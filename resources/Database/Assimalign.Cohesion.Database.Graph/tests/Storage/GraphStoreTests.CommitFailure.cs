using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

public sealed partial class GraphStoreTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Bracket commit failure: unmarked writers preserve directories and the original failure")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_UnmarkedBracketCommitFailure_ShouldPreserveDirectory(bool update)
    {
        // Arrange: the first labeled insert must not leave a token for a rolled-back tree.
        await using var fixture = new Fixture();
        if (update)
        {
            var seed = await fixture.Begin();
            await fixture.Store.CreateNodeAsync(new StoredGraphNode(1, ["Person"], Properties("name", "base")), TestLabelIds.All, seed);
            await fixture.Coordinator.CommitAsync(seed);
        }
        var writer = await fixture.Begin();
        var failure = new IOException("Injected bracket commit failure");
        int commits = 0;
        SetTransactionCommitting(fixture.Storage, bracket =>
        {
            bracket.IsActive.ShouldBeTrue();
            commits++;
            throw failure;
        });
        try
        {
            // Act / Assert: repeat while the same unmarked writer remains active.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var observed = await Record.ExceptionAsync(async () =>
                {
                    if (update)
                    { await fixture.Store.UpdateNodeAsync(1, ["Person"], Properties("name", "changed"), TestLabelIds.All, writer); }
                    else
                    { await fixture.Store.CreateNodeAsync(new StoredGraphNode(1, ["Person"], _empty), TestLabelIds.All, writer); }
                });
                observed.ShouldBeSameAs(failure);
                fixture.Store.EstimateLabelCount("Person").ShouldBe(update ? 1 : 0);
                LabelTokens(fixture.Store).Count.ShouldBe(update ? 1 : 0);
                DirectoryIds(fixture.Store, 1, null).ShouldBe(update ? new ulong[] { 1 } : []);
                var nodes = fixture.Store.StreamLabels(["Person"], writer.Snapshot, CancellationToken.None).ToArray();
                nodes.Length.ShouldBe(update ? 1 : 0);
                if (update) { nodes[0].Properties["name"].ShouldBe("base"); }
                fixture.Coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
            }
            commits.ShouldBe(2);
        }
        finally { SetTransactionCommitting(fixture.Storage, null); }
        await fixture.Coordinator.RollbackAsync(writer);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_TransactionCommitting")]
    private static extern void SetTransactionCommitting(global::Assimalign.Cohesion.Database.Storage.Storage storage, Action<StorageTransaction>? observer);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Ids")]
    private static extern ulong[] DirectoryIds(GraphStore store, byte kind, Action<long>? reserve);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_labelTokens")]
    private static extern ref Dictionary<string, HashSet<Guid>> LabelTokens(GraphStore store);
}
