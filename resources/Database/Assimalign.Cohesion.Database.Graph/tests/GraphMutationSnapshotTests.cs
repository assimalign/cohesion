using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphMutationSnapshotTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Writer snapshot: refresh retains the original pin and writes no journal records (#1149)")]
    public async Task LockWriter_RefreshReadCommittedSnapshot_ShouldKeepPinWithoutJournaling()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("refresh-pin", new());
        await using var db = await engine.CreateDatabaseAsync("graph", token);
        await using var writer = await db.CreateSessionAsync(token);
        await using var reader = await db.CreateSessionAsync(token);
        var node = await db.CreateNodeAsync(writer, ["N"], new Dictionary<string, object?> { ["x"] = 0L }, token);
        var operation = await db.BeginOperationAsync(reader, token);
        try
        {
            var original = operation.Context;
            db.Store.FindNode(node.Id.Value, original.Snapshot)!.Value.Properties["x"].ShouldBe(0L);
            await writer.ExecuteAsync("MATCH (n:N) SET n.x=1", cancellationToken: token);
            var contexts = db.Coordinator.GetOpenContexts().OrderBy(context => context.Sequence).ToArray();
            contexts.Length.ShouldBe(2);
            long journalLength = db.DataStorage.JournalLength;

            // Act
            await db.LockWriterAsync(operation, token);
            await db.LockWriterAsync(operation, token);

            // Assert
            operation.Context.ShouldNotBeSameAs(original);
            db.Store.FindNode(node.Id.Value, operation.Context.Snapshot)!.Value.Properties["x"].ShouldBe(1L);
            db.Store.FindNode(node.Id.Value, original.Snapshot)!.Value.Properties["x"].ShouldBe(0L);
            db.Coordinator.GetOpenContexts().OrderBy(context => context.Sequence).ShouldBe(contexts);
            db.DataStorage.JournalLength.ShouldBe(journalLength);
            await operation.CompleteAsync();
            db.Coordinator.GetOpenContexts().ShouldBeEmpty();
        }
        catch (Exception error) { await operation.AbortAsync(error); throw; }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Public mutations: queued ReadCommitted refreshes after locking and Snapshot retains conflicts (#1149)")]
    [InlineData("Label", 0)]
    [InlineData("Label", 1)]
    [InlineData("Label", 2)]
    [InlineData("NodeKey", 0)]
    [InlineData("NodeKey", 1)]
    [InlineData("NodeKey", 2)]
    [InlineData("RelationshipType", 0)]
    [InlineData("RelationshipType", 1)]
    [InlineData("RelationshipType", 2)]
    [InlineData("DeleteNode", 0)]
    [InlineData("DeleteNode", 1)]
    [InlineData("DeleteNode", 2)]
    [InlineData("DeleteRelationship", 0)]
    [InlineData("DeleteRelationship", 1)]
    [InlineData("DeleteRelationship", 2)]
    [InlineData("Schema", 0)]
    [InlineData("Schema", 1)]
    [InlineData("Schema", 2)]
    public async Task Mutate_QueuedBehindCommittedWriter_ShouldHonorIsolation(string scenario, int mode)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("mutation-snapshots", new());
        int queued = 0, completed = 0, conflicts = 0;
        var elapsed = Stopwatch.StartNew();
        do
        {
            await using var db = await engine.CreateDatabaseAsync("case" + queued, token);
            await using var first = await db.CreateSessionAsync(token);
            await using var second = await db.CreateSessionAsync(token);
            var a = await db.CreateNodeAsync(first, ["N"], new Dictionary<string, object?> { ["x"] = 0L }, token);
            var b = await db.CreateNodeAsync(first, [], cancellationToken: token);
            var edge = await db.CreateRelationshipAsync(first, a.Id, b.Id, "R", new Dictionary<string, object?> { ["x"] = 0L }, token);
            await using var writer = await first.BeginTransactionAsync(IsolationLevel.Snapshot, token);
            await using var transaction = mode == 0 ? null : await second.BeginTransactionAsync(
                mode == 1 ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot, token);
            var label = new GraphLabelMetadata(Guid.NewGuid(), "Fresh");
            var type = new GraphRelationshipTypeMetadata(Guid.NewGuid(), "FreshR");
            var schema = GraphSchema.Open(db, first);
            switch (scenario)
            {
                case "Label": case "Schema": await schema.SaveLabelAsync(label, token); break;
                case "NodeKey": await db.CreateNodeAsync(first, ["N"], new Dictionary<string, object?> { ["fresh"] = 1L }, token); break;
                case "RelationshipType": await schema.SaveRelationshipTypeAsync(type, token); break;
                case "DeleteNode": await first.ExecuteAsync("MATCH (n:N) SET n.x=1", cancellationToken: token); break;
                case "DeleteRelationship": await first.ExecuteAsync("MATCH ()-[r:R]->() SET r.x=1", cancellationToken: token); break;
            }

            // Act: the call runs synchronously as far as its writer-lock wait.
            var pending = MutateAsync();
            pending.IsCompleted.ShouldBeFalse();
            queued++;
            await writer.CommitAsync(token);

            // Assert
            if (mode == 2)
            {
                if (scenario == "Schema") { await Should.ThrowAsync<GraphCatalogException>(() => pending); }
                else { await Should.ThrowAsync<DatabaseTransactionAbortedException>(() => pending); }
                transaction!.State.ShouldBe(TransactionState.Faulted);
                await transaction.RollbackAsync(token);
                conflicts++;
            }
            else
            {
                await pending.WaitAsync(token);
                if (transaction is not null) { await transaction.CommitAsync(token); }
                completed++;
                if (scenario == "DeleteNode") { (await db.GetNodeAsync(first, a.Id, token)).ShouldBeNull(); }
                if (scenario == "DeleteRelationship")
                {
                    var read = await db.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
                    (await db.Store.GetIncidentAsync(a.Id.Value, read.Snapshot, token)).ShouldBeEmpty();
                    await db.Coordinator.RollbackAsync(read, token);
                }
            }

            async Task MutateAsync()
            {
                switch (scenario)
                {
                    case "Label":
                        (await db.CreateNodeAsync(second, ["Fresh"], cancellationToken: token)).Labels.ShouldContain("Fresh"); break;
                    case "NodeKey":
                        (await db.CreateNodeAsync(second, ["N"], new Dictionary<string, object?> { ["fresh"] = 2L }, token)).Properties["fresh"].ShouldBe(2L); break;
                    case "RelationshipType":
                        (await db.CreateRelationshipAsync(second, a.Id, b.Id, "FreshR", cancellationToken: token)).Type.ShouldBe("FreshR"); break;
                    case "DeleteNode": (await db.DeleteNodeAsync(second, a.Id, token)).ShouldBeTrue(); break;
                    case "DeleteRelationship": (await db.DeleteRelationshipAsync(second, edge.Id, token)).ShouldBeTrue(); break;
                    case "Schema":
                        await GraphSchema.Open(db, second).SaveLabelAsync(label, token);
                        (await GraphSchema.Open(db, second).GetLabelsAsync(token)).Single(item => item.Name == "Fresh").ShouldBe(label); break;
                }
            }
        } while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || queued < 2) && elapsed.Elapsed < TimeSpan.FromSeconds(20));
        queued.ShouldBeGreaterThanOrEqualTo(2);
        completed.ShouldBe(mode == 2 ? 0 : queued);
        conflicts.ShouldBe(mode == 2 ? queued : 0);
    }
}
