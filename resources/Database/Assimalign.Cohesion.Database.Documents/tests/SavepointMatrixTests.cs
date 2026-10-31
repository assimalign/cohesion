using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Savepoint engine races")]
public sealed class SavepointMatrixTests
{
    public static IEnumerable<object[]> UndoFailures => SavepointUndoFault.Cases();

    [Theory(DisplayName = "Cohesion Test [Documents] - Every undo stage recovers by target, parent or root with an exact subsequent commit (#1726)")]
    [MemberData(nameof(UndoFailures))]
    public async Task ExecuteAsync_EachUndoStage_ShouldRecoverExactCommit(int stage, int recovery)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = DocumentDatabaseEngine.Create("undo-matrix", new());
        var database = await engine.CreateDatabaseAsync("db", token);
        await using var session = await database.CreateSessionAsync(token);
        await Execute("CREATE COLLECTION items");
        await Execute("INSERT INTO items (KEY, VALUE) VALUES ('k1', {'n': 1})");
        await Execute("BEGIN");
        await Execute("INSERT INTO items (KEY, VALUE) VALUES ('k2', {'n': 2})");
        await Execute("SAVEPOINT outer_scope");
        await Execute("INSERT INTO items (KEY, VALUE) VALUES ('k3', {'n': 3})");
        await Execute("SAVEPOINT target");
        await Execute("INSERT INTO items (KEY, VALUE) VALUES ('k4', {'n': 4})");
        for (int i = 0; i < 100; i++) { await Execute("UPDATE items SET n = n + 1 WHERE n >= 4"); }
        await Execute("SAVEPOINT child");
        var context = session.CurrentTransaction!.Context;
        using var fault = new SavepointUndoFault(database.Coordinator, context, stage);

        // Act
        try
        {
            var failure = await Should.ThrowAsync<DatabaseException>(() => Execute("ROLLBACK TO target"));
            failure.Diagnostics.ShouldContain(item => item.Code == "CK04009");
            fault.IsCauseOf(failure).ShouldBeTrue("translation must retain the injected exception object");
            fault.Calls.ShouldBe(stage < 4 ? 1 : stage - 3);
            TransactionCoordinator.IsPartialUndoPending(context).ShouldBeTrue();
            foreach (string statement in new[] { "SELECT n FROM items ORDER BY n", "COMMIT", "SAVEPOINT refused", "RELEASE target", "ROLLBACK TO child" })
            {
                var refusal = await Should.ThrowAsync<DatabaseException>(() => Execute(statement));
                refusal.Diagnostics.ShouldContain(item => item.Code == "CK04009");
            }
        }
        finally { fault.Disarm(); }

        // Assert: the suffix's unique values/identities can be reused after repair.
        await Execute(recovery == 2 ? "ROLLBACK" : recovery == 1 ? "ROLLBACK TO outer_scope" : "ROLLBACK TO target");
        TransactionCoordinator.IsPartialUndoPending(context).ShouldBeFalse();
        if (stage is 2 or 3) { fault.Value.ShouldBe(7); }
        if (recovery == 2) { await Execute("BEGIN"); }
        else { await Execute(recovery == 1 ? "ROLLBACK TO outer_scope" : "ROLLBACK TO target"); }
        await Execute("INSERT INTO items (KEY, VALUE) VALUES ('k4', {'n': 4})");
        await Execute("COMMIT");
        await using var observer = await database.CreateSessionAsync(token);
        await using var result = (await observer.ExecuteAsync("SELECT n FROM items ORDER BY n", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        values.ShouldBe(recovery == 2 ? new long[] { 1, 4 } : recovery == 1 ? new long[] { 1, 2, 4 } : new long[] { 1, 2, 3, 4 });

        async Task Execute(string text)
            => (await session.ExecuteAsync(text, cancellationToken: token)).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Live crash images after rollback-to, during undo and after commit recover exactly (#1726)")]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public async Task OpenDatabaseAsync_EachUndoCrashPhase_ShouldRecoverProvenPrefix(int phase, int transportMode)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = DocumentDatabaseEngine.Create("crash-matrix", new() { StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source", token);
        await using var session = await database.CreateSessionAsync(token);
        await using IDuplexConnectionListener? listener = transportMode == 0 ? null : transportMode == 2
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = listener is null ? null : DocumentDatabaseServer.Create(engine, new() { Listener = listener });
        if (server is not null) { await server.StartAsync(token); }
        IDuplexConnectionFactory? factory = listener is null ? null : transportMode == 2
            ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory();
        await using var transport = factory is null ? null : await factory.ConnectAsync(listener!.EndPoint, token);
        await using var channel = transport is null ? null : new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, true);
        var wire = channel is null ? null : new SavepointWireControl(channel, (ProtocolMessageType)DocumentProtocolMessageType.Execute, (ProtocolMessageType)DocumentProtocolMessageType.Failure,
            text => DocumentProtocolExecuteMessage.Create(text).Encode(), frame => DocumentProtocolFailureMessage.Decode(frame.Payload.Span).Message, token);
        if (wire is not null) { await wire.StartAsync("source"); }

        await Execute("CREATE COLLECTION items");
        await Execute("INSERT INTO items (KEY, VALUE) VALUES ('k1', {'n': 1})");
        await Execute("BEGIN");
        await Execute("INSERT INTO items (KEY, VALUE) VALUES ('k2', {'n': 2})");
        await Execute("SAVEPOINT target");
        await Execute("INSERT INTO items (KEY, VALUE) VALUES ('k4', {'n': 4})");
        for (int i = 0; i < 100; i++) { await Execute("UPDATE items SET n = n + 1 WHERE n >= 4"); }
        int captured = 0;
        int batches = 0;
        if (phase == 1)
        {
            Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoMarkerDriver.SetBatchObserver(database.Coordinator, () =>
            {
                if (++batches != 2) { return; }
                Capture();
                throw new IOException("Crash image captured during the second undo bracket");
            });
        }

        // Act
        try
        {
            if (phase == 1)
            {
                if (wire is null)
                { (await Should.ThrowAsync<DatabaseException>(() => Execute("ROLLBACK TO target"))).Diagnostics.ShouldContain(item => item.Code == "CK04009"); }
                else
                {
                    (await wire.ExecuteAsync("ROLLBACK TO target")).ShouldBeFalse();
                    wire.LastFailure.ShouldNotBeNull().ShouldStartWith("CK04009:", Case.Sensitive);
                }
            }
            else
            {
                await Execute("ROLLBACK TO target");
                if (phase == 2) { await Execute("COMMIT"); }
                Capture();
            }
        }
        finally { Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoMarkerDriver.SetBatchObserver(database.Coordinator, null); }

        // Assert: open copied bytes before the source transaction/session is cleaned up.
        captured.ShouldBe(1);
        if (phase == 1) { batches.ShouldBe(2); }
        var image = await engine.OpenDatabaseAsync("image", token);
        await using var reader = await image.CreateSessionAsync(token);
        await using var result = (await reader.ExecuteAsync("SELECT n FROM items ORDER BY n", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        values.ShouldBe(phase == 2 ? new long[] { 1, 2 } : new long[] { 1 });

        void Capture()
        {
            foreach (var storage in new[] { database.DataStorage })
            {
                storage.WriteBackDirtyPages(int.MaxValue);
                storage.WriteAheadJournal.Flush(forceDurable: false);
            }
            strategy.CopyDatabase("source", "image");
            captured++;
        }
        async Task Execute(string text)
        {
            if (wire is null) { (await session.ExecuteAsync(text, cancellationToken: token)).Status.ShouldBe(DatabaseResultStatus.Success); }
            else { (await wire.ExecuteAsync(text)).ShouldBeTrue(wire.LastFailure); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Session disposal and database close drain admitted undo before releasing kernel ownership (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_AdmittedControl_ShouldDrainBeforeClose(bool closeDatabase)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = DocumentDatabaseEngine.Create("close-matrix", new());
        var database = await engine.CreateDatabaseAsync("db", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: token);
        await session.ExecuteAsync("BEGIN", cancellationToken: token);
        await session.ExecuteAsync("SAVEPOINT target", cancellationToken: token);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('k4', {'n': 4})", cancellationToken: token);
        var context = session.CurrentTransaction!.Context;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int admitted = 0;
        Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoMarkerDriver.SetBatchObserver(database.Coordinator, () =>
        {
            Interlocked.Increment(ref admitted);
            entered.TrySetResult();
            release.Wait(token);
        });
        var undo = Task.Run(async () => await session.ExecuteAsync("ROLLBACK TO target", cancellationToken: token), token);
        Task? close = null;

        // Act
        try
        {
            await entered.Task.WaitAsync(token);
            close = closeDatabase ? database.DisposeAsync().AsTask() : session.DisposeAsync().AsTask();
            close.IsCompleted.ShouldBeFalse("physical undo still owns admission and the transaction end gate");
            context.State.ShouldBe(TransactionState.Active);
            if (closeDatabase)
            { await Should.ThrowAsync<ObjectDisposedException>(async () => await database.CreateSessionAsync(token)); }
            else
            { session.State.ShouldBe(SessionState.Closed); }
        }
        finally
        {
            release.Set();
            try { await undo.WaitAsync(token); }
            finally
            {
                Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoMarkerDriver.SetBatchObserver(database.Coordinator, null);
                if (close is not null) { await close.WaitAsync(token); }
            }
        }

        // Assert
        admitted.ShouldBe(1);
        context.State.ShouldNotBe(TransactionState.Active);
        TransactionCoordinator.IsPartialUndoPending(context).ShouldBeFalse();
        if (!closeDatabase)
        {
            await using var reader = await database.CreateSessionAsync(token);
            await using var result = (await reader.ExecuteAsync("SELECT n FROM items", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
            var rows = new List<long>();
            await foreach (var row in result.GetRowsAsync(token)) { rows.Add(Convert.ToInt64(row.GetValue(0))); }
            rows.ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Unknown savepoint controls keep the model's ordinary failure rule (#1726)")]
    [InlineData("RELEASE absent_scope")]
    [InlineData("ROLLBACK TO absent_scope")]
    public async Task ExecuteAsync_UnknownControl_ShouldKeepModelFailureRule(string control)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("failed-controls", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("SAVEPOINT retained", cancellationToken: CancellationToken.None);
        var transaction = session.CurrentTransaction!;

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(control, cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        transaction.State.ShouldBe(TransactionState.Faulted);
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ROLLBACK TO retained", cancellationToken: CancellationToken.None));
        refused.Diagnostics.ShouldContain(item => item.Code == "CD04101");
        await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Saturated write streams refuse CK04011, preserve CK04010 and drain without wrapping (#1726)")]
    public async Task OpenWrite_SaturatedRegistration_ShouldPreserveControlRefusalAndDrain()
    {
        // Arrange: register counts without allocating 65,535 content buffers.
        await using var engine = DocumentDatabaseEngine.Create("stream-matrix", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        var context = transaction.Context;
        var marker = await database.Coordinator.CreateSavepointAsync(context, CancellationToken.None);
        int registered = 0;
        try
        {
            for (; registered < 65_535; registered++) { SavepointStreamCount.Begin(context); }

            // Act
            var failure = Record.Exception(() => database.DataStorage.OpenWrite(database.Coordinator, context,
                static _ => ValueTask.CompletedTask, static () => ValueTask.CompletedTask, CancellationToken.None));

            // Assert
            failure.ShouldNotBeNull();
            TransactionCoordinator.GetUndoFailureCode(failure).ShouldBe("CK04011");
            var refused = await Record.ExceptionAsync(() => database.Coordinator.RollbackToSavepointAsync(context, marker, CancellationToken.None).AsTask());
            TransactionCoordinator.GetUndoFailureCode(refused!).ShouldBe("CK04010");
        }
        finally { for (int i = 0; i < registered; i++) { SavepointStreamCount.End(context); } }
        await using (var stream = database.DataStorage.OpenWrite(database.Coordinator, context,
            static _ => ValueTask.CompletedTask, static () => ValueTask.CompletedTask, CancellationToken.None))
        { await stream.WriteAsync(new byte[] { 1 }, CancellationToken.None); }
        await database.Coordinator.RollbackToSavepointAsync(context, marker, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Both reader isolations progress inside admitted undo and scoped lock waiters wake in every execution mode (#1726)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_ReadersAndLockWaiters_ShouldProgressDuringUndo(int transportMode)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = DocumentDatabaseEngine.Create("reader-matrix", new());
        var database = await engine.CreateDatabaseAsync("source", token);
        await using var session = await database.CreateSessionAsync(token);
        await using IDuplexConnectionListener? listener = transportMode == 0 ? null : transportMode == 2
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = listener is null ? null : DocumentDatabaseServer.Create(engine, new() { Listener = listener });
        if (server is not null) { await server.StartAsync(token); }
        IDuplexConnectionFactory? factory = listener is null ? null : transportMode == 2
            ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory();
        await using var transport = factory is null ? null : await factory.ConnectAsync(listener!.EndPoint, token);
        await using var channel = transport is null ? null : new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, true);
        var wire = channel is null ? null : new SavepointWireControl(channel, (ProtocolMessageType)DocumentProtocolMessageType.Execute, (ProtocolMessageType)DocumentProtocolMessageType.Failure,
            text => DocumentProtocolExecuteMessage.Create(text).Encode(), frame => DocumentProtocolFailureMessage.Decode(frame.Payload.Span).Message, token);
        if (wire is not null) { await wire.StartAsync("source"); }
        await Execute("CREATE COLLECTION items");
        await Execute("INSERT INTO items (KEY, VALUE) VALUES ('k1', {'n': 1})");
        await using var snapshotReader = await database.CreateSessionAsync(token);
        await using var snapshot = await snapshotReader.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        await using var freshReader = await database.CreateSessionAsync(token);
        await using var waiter = await database.CreateSessionAsync(token);
        await Execute("BEGIN");
        await Execute("SAVEPOINT target");
        var writer = wire is null ? session : (DocumentDatabaseSession)server!.Sessions.Single().DatabaseSession!;
        var context = writer.CurrentTransaction!.Context;
        int grants = 0;

        // Act: each read must start and finish in the same real undo callback interval.
        await Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoReaderProbe.RunAsync(async () =>
        {
            await Read(snapshotReader);
            await using var fresh = await freshReader.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await Read(freshReader);
            await fresh.CommitAsync(token);
        }, async () =>
        {
            await Execute("INSERT INTO items (KEY, VALUE) VALUES ('k4', {'n': 4})");
            await using var waitingTransaction = await waiter.BeginTransactionAsync(cancellationToken: token);
            var resource = LockResource.Entry(999, 123);
            await database.Coordinator.LockManager.AcquireAsync(context.Sequence, resource, LockMode.Exclusive, token);
            var waiting = database.Coordinator.LockManager.AcquireAsync(waitingTransaction.Context.Sequence, resource, LockMode.Exclusive, token).AsTask();
            waiting.IsCompleted.ShouldBeFalse();
            Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoMarkerDriver.SetBatchObserver(database.Coordinator,
                Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoReaderProbe.BatchObserver);
            try { await Execute("ROLLBACK TO target"); }
            finally { Assimalign.Cohesion.Database.Transactions.Tests.TestObjects.UndoMarkerDriver.SetBatchObserver(database.Coordinator, null); }
            await waiting.WaitAsync(token);
            grants++;
            await waitingTransaction.RollbackAsync(token);
        });

        // Assert
        grants.ShouldBeGreaterThanOrEqualTo(8);
        await Execute("COMMIT");
        await Read(session);

        async Task Read(DocumentDatabaseSession reader)
        {
            await using var result = (await reader.ExecuteAsync("SELECT n FROM items ORDER BY n", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
            var values = new List<long>();
            await foreach (var row in result.GetRowsAsync(token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
            values.ShouldBe(new long[] { 1 });
        }
        async Task Execute(string text)
        {
            if (wire is null) { (await session.ExecuteAsync(text, cancellationToken: token)).Status.ShouldBe(DatabaseResultStatus.Success); }
            else { (await wire.ExecuteAsync(text)).ShouldBeTrue(wire.LastFailure); }
        }
    }
}
