using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsignedValues_ShouldRespectPromotedSchemaTypesAndRetainTheirStoredValue(bool relationship)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var properties = new Dictionary<string, object?>
        {
            ["small"] = byte.MaxValue,
            ["medium"] = ushort.MaxValue,
            ["large"] = uint.MaxValue,
            ["largest"] = ulong.MaxValue
        };
        var first = await database.CreateNodeAsync(session, ["Person"], relationship ? null : properties);
        var second = relationship ? await database.CreateNodeAsync(session, ["Person"]) : first;
        if (relationship) { await database.CreateRelationshipAsync(session, first.Id, second.Id, "FRIEND", properties); }
        var schema = GraphSchema.Open(database, session);
        Guid id = relationship ? (await schema.GetRelationshipTypesAsync())[0].Id : (await schema.GetLabelsAsync())[0].Id;
        await schema.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(id, "small", DatabaseType.Int16, true));
        await schema.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(id, "medium", DatabaseType.Int32, true));
        await schema.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(id, "large", DatabaseType.Int64, true));
        await schema.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(id, "largest", DatabaseType.Decimal, true));
        IReadOnlyDictionary<string, object?> actual;
        if (relationship)
        {
            actual = (await database.CreateRelationshipAsync(session, first.Id, second.Id, "FRIEND", properties)).Properties;
        }
        else
        {
            var created = await database.CreateNodeAsync(session, ["Person"], properties);
            actual = (await database.GetNodeAsync(session, created.Id)).ShouldNotBeNull().Properties;
        }
        foreach (var item in properties) { actual[item.Key].ShouldBe(item.Value); }
    }

    [Theory]
    [InlineData(DatabaseType.Null)]
    [InlineData(DatabaseType.Binary)]
    [InlineData(DatabaseType.Date)]
    [InlineData(DatabaseType.Time)]
    [InlineData(DatabaseType.DateTime)]
    [InlineData(DatabaseType.DateTimeOffset)]
    [InlineData(DatabaseType.TimeSpan)]
    [InlineData(DatabaseType.Guid)]
    [InlineData(DatabaseType.Json)]
    [InlineData(DatabaseType.JsonBinary)]
    public async Task UnsupportedSchemaScalar_ShouldBeRejectedWithoutPublishingMetadata(DatabaseType unsupported)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        await schema.SaveLabelAsync(label);
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await schema.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(label.Id, "value", unsupported)));
        error.Message.ShouldContain("COHDBG003");
        error.Message.ShouldContain(unsupported.ToString());
        (await schema.GetPropertyKeysAsync(label.Id)).ShouldBeEmpty();
        await database.CreateNodeAsync(session, ["Person"]);
    }

    [Fact]
    public async Task ClosingSessionWhileWaitingForWriter_ShouldAbortAndReleaseALaterGrant()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var blocker = await database.CreateSessionAsync();
        await using var waiting = await database.CreateSessionAsync();
        await using var observer = await database.CreateSessionAsync();
        await database.CreateNodeAsync(blocker, ["Person"]);
        await using var transaction = await blocker.BeginTransactionAsync();
        await database.CreateNodeAsync(blocker, ["Person"]);
        var pending = database.CreateNodeAsync(waiting, ["Person"]).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await Should.ThrowAsync<DatabaseException>(async () => await database.CreateNodeAsync(waiting, ["Person"]));
        await Should.ThrowAsync<DatabaseException>(async () => await waiting.BeginTransactionAsync());
        await waiting.DisposeAsync();
        waiting.State.ShouldBe(SessionState.Closed);
        await transaction.RollbackAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Should.ThrowAsync<DatabaseException>(async () => await pending.WaitAsync(timeout.Token));
        var next = await database.CreateNodeAsync(observer, ["Person"], cancellationToken: timeout.Token);
        (await database.GetNodeAsync(observer, next.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task CancellingWriterWait_ShouldReleaseSessionAndLeaveOtherTransactionsUsable()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var blocker = await database.CreateSessionAsync();
        await using var waiting = await database.CreateSessionAsync();
        await database.CreateNodeAsync(blocker, ["Person"]);
        await using var transaction = await blocker.BeginTransactionAsync();
        await database.CreateNodeAsync(blocker, ["Person"]);
        using var cancellation = new CancellationTokenSource();
        var pending = database.CreateNodeAsync(waiting, ["Person"], cancellationToken: cancellation.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        await transaction.CommitAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (await database.CreateNodeAsync(waiting, ["Person"], cancellationToken: timeout.Token)).Id.Value.ShouldBeGreaterThan(0UL);
    }

    [Fact]
    public async Task StaleSnapshot_ShouldRejectNodeWriteAfterPropertyConstraintChanged()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var stale = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        var schema = GraphSchema.Open(database, writer);
        await schema.SaveLabelAsync(label);
        await using var transaction = await stale.BeginTransactionAsync();
        await schema.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(label.Id, "age", DatabaseType.Int64, true));
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await database.CreateNodeAsync(stale, ["Person"]));
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await database.CreateNodeAsync(writer, ["Person"], new Dictionary<string, object?> { ["age"] = 42L })).Properties["age"].ShouldBe(42L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleDrop_ShouldRefuseNewlyCommittedGraphData(bool relationship)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var stale = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        var writerSchema = GraphSchema.Open(database, writer);
        await writerSchema.SaveLabelAsync(new GraphLabelMetadata(Guid.NewGuid(), "Person"));
        await writerSchema.SaveRelationshipTypeAsync(new GraphRelationshipTypeMetadata(Guid.NewGuid(), "FRIEND"));
        await using var transaction = await stale.BeginTransactionAsync();
        var first = await database.CreateNodeAsync(writer, ["Person"]);
        if (relationship)
        {
            var second = await database.CreateNodeAsync(writer, ["Person"]);
            await database.CreateRelationshipAsync(writer, first.Id, second.Id, "FRIEND");
        }
        var schema = GraphSchema.Open(database, stale);
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (relationship) { await schema.DropRelationshipTypeAsync("FRIEND"); }
            else { await schema.DropLabelAsync("Person"); }
        });
        error.Message.ShouldContain("in use");
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await writerSchema.GetLabelsAsync()).ShouldHaveSingleItem().Name.ShouldBe("Person");
        (await writerSchema.GetRelationshipTypesAsync()).ShouldHaveSingleItem().Name.ShouldBe("FRIEND");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PropertyConstraintChange_ShouldValidateExistingNodesAndRelationships(bool relationship)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var first = await database.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["age"] = "unknown" });
        var second = await database.CreateNodeAsync(session, ["Person"]);
        if (relationship)
        {
            await database.CreateRelationshipAsync(session, first.Id, second.Id, "FRIEND", new Dictionary<string, object?> { ["age"] = "unknown" });
        }
        var schema = GraphSchema.Open(database, session);
        Guid definitionId = relationship
            ? (await schema.GetRelationshipTypesAsync())[0].Id
            : (await schema.GetLabelsAsync())[0].Id;
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await schema.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(definitionId, "age", DatabaseType.Int64, true)));
        error.Message.ShouldContain("COHDBG003");
        (await schema.GetPropertyKeysAsync(definitionId)).ShouldHaveSingleItem().Type.ShouldBeNull();
        (await database.GetNodeAsync(session, first.Id)).ShouldNotBeNull().Properties["age"].ShouldBe("unknown");
    }
}
