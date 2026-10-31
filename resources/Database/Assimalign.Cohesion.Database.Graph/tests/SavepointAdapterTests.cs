using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection("Savepoint engine races")]
public sealed class SavepointAdapterTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Apply admission: a claimed host rollback cannot orphan late directory undo")]
    public async Task CreateNode_RollbackClaimedAfterApply_ShouldLeaveNoLateLedger()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("admission", new());
        var database = await engine.CreateDatabaseAsync("source");
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await UndoMarkerDriver.CreateAsync(database.Coordinator, writer);
        var pause = new PausingUndo();
        await database.Coordinator.ApplyStatementAsync(writer, _ =>
        {
            database.Coordinator.VersionStore.RecordIndexEntryCreated(writer.Sequence, pause, ReadOnlyMemory<byte>.Empty, 0);
            return 0;
        });
        Task? rollback = null;
        database.Store.AfterApply = () =>
        {
            rollback = database.Coordinator.RollbackAsync(writer).AsTask();
            pause.Entered.ShouldBeTrue();
            rollback.IsCompleted.ShouldBeFalse();
        };
        try
        {
            // Act: rollback removes the old ledger and pauses before root cleanup.
            await database.Store.CreateNodeAsync(new StoredGraphNode(1, [], new Dictionary<string, object?>()),
                new Dictionary<string, Guid>(), writer);
        }
        finally
        {
            database.Store.AfterApply = null;
            pause.Resume.TrySetResult();
            if (rollback is not null) { await rollback.WaitAsync(TimeSpan.FromSeconds(30)); }
        }
        // Assert
        writer.State.ShouldBe(TransactionState.RolledBack);
        database.Coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
        await using var reader = await database.CreateSessionAsync();
        (await Values(reader, "MATCH (n) RETURN n.value")).ShouldBeEmpty();
    }

    private sealed class PausingUndo : RecordVersionIndex
    {
        internal bool Entered { get; private set; }
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken)
        { Entered = true; await Resume.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken) => default;
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Savepoint adapters: catalog tokens, constraints, graph directories and indexes follow undo and recovery")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_ModelWrites_ShouldMatchOracleAndCrashImage(bool commit)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = GraphDatabaseEngine.Create("savepoints", new() { StorageStrategy = strategy, CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Item {value: 0})");
        var schema = GraphSchema.Open(database, session);
        await schema.CreateIndexAsync("Item", "by_value", "value");
        var label = (await schema.GetLabelsAsync()).Single(item => item.Name == "Item");
        await using var observer = await database.CreateSessionAsync();
        await using var observation = await observer.BeginTransactionAsync();
        await using var committedObserver = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("MATCH (n:Item) SET n.value = 1");
        (await session.ExecuteAsync("SAVEPOINT marker")).Status.ShouldBe(DatabaseResultStatus.Success);
        await session.ExecuteAsync("MATCH (n:Item) SET n.value = 2");
        await session.ExecuteAsync("INSERT (:Transient {value: 9})");
        await database.Catalog.CreateConstraintAsync(new GraphConstraintMetadata(label.Id, "marker_rule", "value", DatabaseType.Int64), transaction.Context);
        // Act
        await UndoReaderProbe.RunAsync(async () =>
        {
            (await Values(observer, "MATCH (n:Item) RETURN n.value")).ShouldBe([0L]);
            await using var fresh = await committedObserver.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            (await Values(committedObserver, "MATCH (n:Item) RETURN n.value")).ShouldBe([0L]);
            await fresh.CommitAsync();
        }, async () =>
        {
            await session.ExecuteAsync("MATCH (n:Item) SET n.value = 2");
            UndoMarkerDriver.SetBatchObserver(database.Coordinator, UndoReaderProbe.BatchObserver);
            try { (await session.ExecuteAsync("ROLLBACK TO marker")).Status.ShouldBe(DatabaseResultStatus.Success); }
            finally { UndoMarkerDriver.SetBatchObserver(database.Coordinator, null); }
        });
        (await Values(session, "MATCH (n:Item) RETURN n.value")).ShouldBe([1L]);
        (await Values(session, "MATCH (n:Transient) RETURN n.value")).ShouldBeEmpty();
        database.Catalog.FindConstraint("marker_rule", transaction.Context.Snapshot).ShouldBeNull();
        await session.ExecuteAsync("MATCH (n:Item) SET n.value = 3");
        await session.ExecuteAsync("INSERT (:Transient {value: 9})");
        if (commit) { await transaction.CommitAsync(); }
        database.DataStorage.WriteBackDirtyPages(int.MaxValue);
        database.DataStorage.WriteAheadJournal.Flush(forceDurable: false);
        strategy.CopyDatabase("source", "image");
        var image = await engine.OpenDatabaseAsync("image");
        await using var reader = await image.CreateSessionAsync();
        // Assert
        (await Values(reader, "MATCH (n:Item) RETURN n.value")).ShouldBe([commit ? 3L : 0L]);
        (await Values(reader, "MATCH (n:Transient) RETURN n.value")).ShouldBe(commit ? new long[] { 9 } : Array.Empty<long>());
    }

    private static async Task<long[]> Values(GraphDatabaseSession session, string text)
    {
        var result = (await session.ExecuteAsync(text)).ShouldBeAssignableTo<DatabaseResultSet>()!;
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync()) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        return values.ToArray();
    }
}
