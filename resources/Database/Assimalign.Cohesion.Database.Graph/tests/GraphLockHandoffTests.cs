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
            await using var stale = await second.BeginTransactionAsync(IsolationLevel.Snapshot, token);
            var firstSchema = GraphSchema.Open(database, first);
            var secondSchema = GraphSchema.Open(database, second);
            switch (path)
            {
                case "DropLabel":
                case "ValidateNodeData": await database.CreateNodeAsync(first, ["Thing"], cancellationToken: token); break;
                case "DropRelationshipType":
                case "ValidateRelationshipData": await database.CreateRelationshipAsync(first, from.Id, to.Id, "LINK", cancellationToken: token); break;
                case "NodeLabel": await firstSchema.DropLabelAsync("Thing", token); break;
                case "RelationshipType": await firstSchema.DropRelationshipTypeAsync("LINK", token); break;
                default:
                    await firstSchema.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(
                        path == "NodeProperties" ? label.Id : type.Id, "required", DatabaseType.String, true), token);
                    break;
            }
            var pending = MutateAsync();
            pending.IsCompleted.ShouldBeFalse();
            blocked++;
            using var gate = new CommitEventGate(database.Name, writer.Context.Sequence.Value);
            var committing = Task.Run(async () => await writer.CommitAsync(token), token);
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
                    error.Message.ShouldContain("COHDBG003");
                    error.Message.ShouldContain(path.StartsWith("Drop", StringComparison.Ordinal) ? "in use" : "Existing graph data violates");
                }
                refused++;
            }
            finally { gate.Release(); await committing.WaitAsync(token); }

            // Assert: the hook and the refusal happened on every iteration.
            gate.Calls.ShouldBe(1);
            gate.TimedOut.ShouldBeFalse();
            stale.State.ShouldBe(TransactionState.Faulted);
            await stale.RollbackAsync(token);
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
