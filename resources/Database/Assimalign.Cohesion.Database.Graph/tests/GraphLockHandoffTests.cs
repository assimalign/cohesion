using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphLockHandoffTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Lock handoff: data validation sees tracked committed writers")]
    [InlineData("DropLabel")]
    [InlineData("DropRelationshipType")]
    [InlineData("ValidateNodeData")]
    [InlineData("ValidateRelationshipData")]
    public async Task SaveSchema_CommittedWriterStillTracked_ShouldRefuseInvalidDefinition(string path)
        => await ExerciseAsync(path, false);

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Lock handoff: entity writes see tracked committed metadata")]
    [InlineData("NodeLabel")]
    [InlineData("RelationshipType")]
    [InlineData("NodeProperties")]
    [InlineData("RelationshipProperties")]
    public async Task CreateEntity_CommittedWriterStillTracked_ShouldRejectStaleSnapshot(string path)
        => await ExerciseAsync(path, true);

    [Theory(DisplayName = "Cohesion Test [Graph] - SET: queued autocommit sees newly committed keys and labels during lock handoff (#1149)")]
    [InlineData("SET a.fresh=1", "SET a.fresh=2")]
    [InlineData("SET a IS Fresh", "SET a IS Fresh, a.queued=TRUE")]
    public async Task Execute_QueuedCatalogDiscovery_ShouldSeeTrackedCommittedWriter(string update, string queuedUpdate)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("graph-handoff", new());
        int queued = 0, completed = 0;
        var elapsed = Stopwatch.StartNew();
        do
        {
            var database = await engine.CreateDatabaseAsync("queued" + queued, token);
            await using var first = await database.CreateSessionAsync(token);
            await using var second = await database.CreateSessionAsync(token);
            await first.ExecuteAsync("INSERT (:P)", cancellationToken: token);
            await using var writer = await first.BeginTransactionAsync(IsolationLevel.Snapshot, token);
            await first.ExecuteAsync("MATCH (a:P) " + update, cancellationToken: token);
            var pending = second.ExecuteAsync("MATCH (a:P) " + queuedUpdate, cancellationToken: token).AsTask();
            pending.IsCompleted.ShouldBeFalse();
            queued++;
            using var gate = new CommitEventGate(database.Name, writer.Context.Sequence.Value);
            var committing = DedicatedTestTask.Run(async () => await writer.CommitAsync(token), token);
            try
            {
                // Act: A has released its lock but remains tracked while B snapshots and validates.
                await gate.Entered.WaitAsync(token);
                database.Coordinator.GetOpenContexts().ShouldContain(context => context.Sequence == writer.Context.Sequence && context.State == TransactionState.Committed);
                (await pending.WaitAsync(token)).AffectedCount.ShouldBe(1);
                completed++;
            }
            finally { gate.Release(); await committing.WaitAsync(token); }

            // Assert
            gate.Calls.ShouldBe(1);
            gate.TimedOut.ShouldBeFalse();
        } while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || queued < 2) && elapsed.Elapsed < TimeSpan.FromSeconds(20));
        queued.ShouldBeGreaterThanOrEqualTo(2);
        completed.ShouldBe(queued);
    }

    private static async Task ExerciseAsync(string path, bool conflict)
    {
        // Arrange: both snapshots predate A's mutation; B queues behind A's writer lock.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("graph-handoff", new());
        var elapsed = Stopwatch.StartNew();
        int blocked = 0, refused = 0;
        do
        {
            await using var database = await engine.CreateDatabaseAsync("handoff" + blocked, token);
            await using var setup = await database.CreateSessionAsync(token);
            var label = new GraphLabelMetadata(Guid.NewGuid(), "Thing");
            var type = new GraphRelationshipTypeMetadata(Guid.NewGuid(), "LINK");
            var schema = GraphSchema.Open(database, setup);
            await schema.SaveLabelAsync(label, token);
            await schema.SaveRelationshipTypeAsync(type, token);
            var from = await database.CreateNodeAsync(setup, [], cancellationToken: token);
            var to = await database.CreateNodeAsync(setup, [], cancellationToken: token);
            await using var first = await database.CreateSessionAsync(token);
            await using var second = await database.CreateSessionAsync(token);
            await using var writer = await first.BeginTransactionAsync(IsolationLevel.Snapshot, token);
            await using var stale = conflict ? await second.BeginTransactionAsync(IsolationLevel.Snapshot, token) : null;
            // Public DDL requires autocommit; stage catalog substrate mutations under the held lock.
            await database.LockWriterAsync(writer.Context, token);
            var secondSchema = GraphSchema.Open(database, second);
            switch (path)
            {
                case "DropLabel":
                case "ValidateNodeData": await database.CreateNodeAsync(first, ["Thing"], cancellationToken: token); break;
                case "DropRelationshipType":
                case "ValidateRelationshipData": await database.CreateRelationshipAsync(first, from.Id, to.Id, "LINK", cancellationToken: token); break;
                case "NodeLabel": await database.Catalog.DeleteLabelAsync(label.Id, writer.Context, token); break;
                case "RelationshipType": await database.Catalog.DeleteRelationshipTypeAsync(type.Id, writer.Context, token); break;
                default:
                    await database.Catalog.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(
                        path == "NodeProperties" ? label.Id : type.Id, "required", DatabaseType.String, true), writer.Context, token);
                    break;
            }
            var pending = MutateAsync();
            pending.IsCompleted.ShouldBeFalse();
            blocked++;
            using var gate = new CommitEventGate(database.Name, writer.Context.Sequence.Value);
            var committing = DedicatedTestTask.Run(async () => await writer.CommitAsync(token), token);
            try
            {
                // Act: hold A after lock release, and let B validate before A is untracked.
                await gate.Entered.WaitAsync(token);
                database.Coordinator.GetOpenContexts().Any(context => context.Sequence == writer.Context.Sequence &&
                    context.State == TransactionState.Committed).ShouldBeTrue();
                if (conflict)
                { await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending.WaitAsync(token)); }
                else
                {
                    var error = await Should.ThrowAsync<DatabaseException>(async () => await pending.WaitAsync(token));
                    error.Message.ShouldContain("CG03003");
                    error.Message.ShouldContain(path.StartsWith("Drop", StringComparison.Ordinal) ? "in use" : "Existing graph data violates");
                }
                refused++;
            }
            finally { gate.Release(); await committing.WaitAsync(token); }

            // Assert: the hook and the refusal happened on every iteration.
            gate.Calls.ShouldBe(1);
            gate.TimedOut.ShouldBeFalse();
            if (stale is not null)
            {
                stale.State.ShouldBe(TransactionState.Faulted);
                await stale.RollbackAsync(token);
            }
            if (!conflict)
            {
                (await schema.GetLabelsAsync(token)).ShouldContain(label);
                (await schema.GetRelationshipTypesAsync(token)).ShouldContain(type);
                (await schema.GetPropertyKeysAsync(path == "ValidateRelationshipData" ? type.Id : label.Id, token)).ShouldBeEmpty();
            }

            async Task MutateAsync()
            {
                switch (path)
                {
                    case "DropLabel": await secondSchema.DropLabelAsync("Thing", token); break;
                    case "DropRelationshipType": await secondSchema.DropRelationshipTypeAsync("LINK", token); break;
                    case "ValidateNodeData":
                    case "ValidateRelationshipData":
                        await secondSchema.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(
                            path == "ValidateNodeData" ? label.Id : type.Id, "required", DatabaseType.String, true), token);
                        break;
                    case "NodeLabel":
                    case "NodeProperties": await database.CreateNodeAsync(second, ["Thing"], cancellationToken: token); break;
                    default: await database.CreateRelationshipAsync(second, from.Id, to.Id, "LINK", cancellationToken: token); break;
                }
            }
        } while ((elapsed.Elapsed < TimeSpan.FromSeconds(1) || blocked < 2) && elapsed.Elapsed < TimeSpan.FromSeconds(20));
        blocked.ShouldBeGreaterThanOrEqualTo(2);
        refused.ShouldBe(blocked);
    }
}
