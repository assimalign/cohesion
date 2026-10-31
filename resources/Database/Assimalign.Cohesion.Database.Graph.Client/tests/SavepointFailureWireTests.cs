using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Tests.TestObjects;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

[Collection("Savepoint wire failures")]
public sealed class SavepointFailureWireTests
{
    public static IEnumerable<object[]> UndoFailures
    {
        get
        {
            foreach (var fault in SavepointUndoFault.Cases())
            {
                if ((int)fault[1] == 1) { continue; }
                yield return [false, fault[0], (int)fault[1] == 2];
                yield return [true, fault[0], (int)fault[1] == 2];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Every undo stage reports CK04009 and Ready E, then recovers exactly on both transports (#1726)")]
    [MemberData(nameof(UndoFailures))]
    public async Task ExecuteAsync_UndoFailure_ShouldKeepReadyAndRecover(bool tcp, int stage, bool wholeRollback)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: tcp);
        var database = harness.Database;
        await using var seed = await database.CreateSessionAsync(token);

        await seed.ExecuteAsync("INSERT (:Item {id: 1, n: 1})", cancellationToken: token);
        var factory = harness.ConnectionFactory;
        await using var transport = await factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family, true);
        await Send(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "graph", "tester")
            { Capabilities = [ProtocolCapability.TransactionStatus.Name, ProtocolCapability.Diagnostics.Name] }.Encode());
        (await Read()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await Send(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        var startup = await Read();
        startup.Type.ShouldBe(ProtocolMessageType.Ready);
        channel.SetAgreement(ProtocolReadyMessage.Decode(startup.Payload.Span));
        await Execute("BEGIN");
        await Execute("INSERT (:Item {id: 2, n: 2})");
        await Execute("SAVEPOINT scope");
        await Execute("INSERT (:Item {id: 3, n: 3})");
        for (int i = 0; i < 100; i++) { await Execute("MATCH (n:Item) WHERE n.id = 3 SET n.n = n.n + 1"); }
        var session = (GraphDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        var coordinator = Coordinator(database);
        using var fault = new SavepointUndoFault(coordinator, Context(session.CurrentTransaction!), stage);

        // Act
        try
        {
            await Send((ProtocolMessageType)GraphProtocolMessageType.Execute, GraphProtocolExecuteMessage.Create("ROLLBACK TO scope").Encode());
            var error = await Read();
            if (error.Type == ProtocolMessageType.Diagnostics) { error = await Read(); }
            error.Type.ShouldBe(ProtocolMessageType.Error);
            var failure = ProtocolErrorMessage.Decode(error.Payload.Span);
            failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            failure.Message.ShouldStartWith("CK04009:", Case.Sensitive);
            var ready = await Read();
            ready.Type.ShouldBe(ProtocolMessageType.Ready);
            ProtocolTransactionStatusMessage.Decode(ready.Payload.Span).Status.ShouldBe(ProtocolTransactionStatus.Failed);
        }
        finally { fault.Disarm(); }

        // Assert
        fault.Calls.ShouldBe(stage < 4 ? 1 : stage - 3);
        await Execute(wholeRollback ? "ROLLBACK" : "ROLLBACK TO scope");
        if (stage is 2 or 3) { fault.Value.ShouldBe(7); }
        if (wholeRollback) { await Execute("BEGIN"); }
        await Execute("INSERT (:Item {id: 4, n: 4})");
        await Execute("COMMIT");
        session.State.ShouldBe(SessionState.Open);
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync(token);
        await using var result = (await observer.ExecuteAsync("MATCH (n:Item) RETURN n.n ORDER BY n.id", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        values.ShouldBe(wholeRollback ? new long[] { 1, 4 } : new long[] { 1, 2, 4 });

        async Task Send(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        { await channel.Writer.WriteFrameAsync(new(type, payload), token); await channel.Writer.FlushAsync(token); }
        async Task<ProtocolFrame> Read() => (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
        async Task Execute(string statement)
        {
            await Send((ProtocolMessageType)GraphProtocolMessageType.Execute, GraphProtocolExecuteMessage.Create(statement).Encode());
            ProtocolFrame frame;
            do { frame = await Read(); frame.Type.ShouldNotBe(ProtocolMessageType.Error); }
            while (frame.Type != ProtocolMessageType.Ready);
            ProtocolTransactionStatusMessage.Decode(frame.Payload.Span).Status.ShouldNotBe(ProtocolTransactionStatus.Failed);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Returning a fenced connection rolls back all scopes and certifies an exact clean lease (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_PendingUndo_ShouldResetBeforeNextLease(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: tcp);
        var database = harness.Database;
        await using var writer = await harness.Client.ConnectAsync(token);

        await writer.ExecuteAsync("INSERT (:Item {id: 1, n: 1})", cancellationToken: token);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("INSERT (:Item {id: 2, n: 2})", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT pooled", cancellationToken: token);
        await writer.ExecuteAsync("INSERT (:Item {id: 3, n: 3})", cancellationToken: token);
        var server = harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null);
        var session = (GraphDatabaseSession)server.DatabaseSession!;
        using var fault = new SavepointUndoFault(Coordinator(database), Context(session.CurrentTransaction!), 3);
        try
        {
            (await Should.ThrowAsync<GraphClientException>(async () => await writer.ExecuteAsync("ROLLBACK TO pooled", cancellationToken: token)))
                .Diagnostics.ShouldContain(item => item.Code == "CK04009");
        }
        finally { fault.Disarm(); }

        // Act
        await writer.DisposeAsync();
        session.CurrentTransaction.ShouldBeNull();
        fault.Value.ShouldBe(7);
        await using var next = await harness.Client.ConnectAsync(token);
        await next.ExecuteAsync("BEGIN", cancellationToken: token);
        harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null).Id.ShouldBe(server.Id);
        var unknown = await Should.ThrowAsync<GraphClientException>(async () => await next.ExecuteAsync("ROLLBACK TO pooled", cancellationToken: token));
        unknown.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        await next.ExecuteAsync("ROLLBACK", cancellationToken: token);
        await next.ExecuteAsync("INSERT (:Item {id: 4, n: 4})", cancellationToken: token);

        // Assert
        var rows = await next.QueryAsync("MATCH (n:Item) RETURN n.n ORDER BY n.id", cancellationToken: token);
        rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(new long[] { 1, 4 });
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Repeated connection return, drop or cancellation during admitted undo drains the writer and releases the pool slot (#1726)")]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task DisposeAsync_AdmittedUndo_ShouldDrainAndReleasePool(bool tcp, int ending)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        int rounds = 0, admitted = 0, completed = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            await using var harness = await GraphClientTestHarness.StartAsync(tcp: tcp);
            var database = harness.Database;
            await using var writer = await harness.Client.ConnectAsync(token);

            await writer.ExecuteAsync("INSERT (:Item {id: 1, n: 1})", cancellationToken: token);
            await writer.ExecuteAsync("BEGIN", cancellationToken: token);
            await writer.ExecuteAsync("INSERT (:Item {id: 2, n: 2})", cancellationToken: token);
            await writer.ExecuteAsync("SAVEPOINT target", cancellationToken: token);
            await writer.ExecuteAsync("INSERT (:Item {id: 3, n: 3})", cancellationToken: token);
            var session = (GraphDatabaseSession)harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null).DatabaseSession!;
            var context = Context(session.CurrentTransaction!);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            Observe(Coordinator(database).VersionStore, () =>
            {
                Interlocked.Increment(ref admitted);
                entered.TrySetResult();
                release.Wait(token);
            });
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
            var undo = writer.ExecuteAsync("ROLLBACK TO target", cancellationToken: caller.Token).AsTask();
            Task? returning = null;

            // Act: the physical undo callback establishes admission before teardown.
            try
            {
                await entered.Task.WaitAsync(token);
                if (ending == 2) { caller.Cancel(); }
                returning = ending == 1 ? writer.AbortAsync().AsTask() : writer.DisposeAsync().AsTask();
            }
            finally
            {
                release.Set();
                try { await Record.ExceptionAsync(async () => await undo.WaitAsync(token)); }
                finally { Observe(Coordinator(database).VersionStore, null); }
                if (returning is not null) { await returning.WaitAsync(token); }
            }

            // Assert: both a retained connector and a discarded peer must free the pool slot.
            await using var next = await harness.Client.ConnectAsync(token);
            await next.ExecuteAsync("BEGIN", cancellationToken: token);
            await next.ExecuteAsync("ROLLBACK", cancellationToken: token);
            while (context.State == TransactionState.Active) { await Task.Delay(1, token); }
            context.State.ShouldBe(TransactionState.RolledBack);
            var rows = await next.QueryAsync("MATCH (n:Item) RETURN n.n ORDER BY n.id", cancellationToken: token);
            rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(new long[] { 1 });
            completed++;
            rounds++;
        }
        while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 8 || admitted < 8 || completed < 8)
            && elapsed.Elapsed < TimeSpan.FromSeconds(45));
        rounds.ShouldBeGreaterThanOrEqualTo(8);
        admitted.ShouldBe(rounds);
        completed.ShouldBe(rounds);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Failed savepoint controls keep the ordinary model failure rule over both transports (#1726)")]
    [InlineData(false, "RELEASE absent_scope")]
    [InlineData(true, "RELEASE absent_scope")]
    [InlineData(false, "ROLLBACK TO absent_scope")]
    [InlineData(true, "ROLLBACK TO absent_scope")]
    public async Task ExecuteAsync_UnknownControl_ShouldKeepModelFailureRule(bool tcp, string control)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: tcp);
        await using var writer = await harness.Client.ConnectAsync(token);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT retained", cancellationToken: token);

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () => await writer.ExecuteAsync(control, cancellationToken: token));

        // Assert
        error.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        var refused = await Should.ThrowAsync<GraphClientException>(async () => await writer.ExecuteAsync("ROLLBACK TO retained", cancellationToken: token));
        refused.Diagnostics.ShouldContain(item => item.Code == "CG04007");
        await writer.ExecuteAsync("ROLLBACK", cancellationToken: token);
    }


    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Cancellation before dispatch admits no control and a discarded lease resets every scope (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CanceledBeforeDispatch_ShouldDiscardAndReset(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: tcp);
        var database = harness.Database;
        await using var writer = await harness.Client.ConnectAsync(token);

        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT retained", cancellationToken: token);
        await writer.ExecuteAsync("INSERT (:Item {n: 4})", cancellationToken: token);
        var session = (GraphDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        var transaction = session.CurrentTransaction!;
        var coordinator = Coordinator(database);
        int admitted = 0;
        Observe(coordinator.VersionStore, () => admitted++);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert: the shared client deliberately breaks a canceled exchange (#1313).
        // The control never entered the kernel; returning that lease must scrub its scope.
        try
        {
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await writer.ExecuteAsync("ROLLBACK TO retained", cancellationToken: canceled.Token));
            admitted.ShouldBe(0);
            session.CurrentTransaction.ShouldBeSameAs(transaction);
            transaction.State.ShouldBe(TransactionState.Active);
        }
        finally { Observe(coordinator.VersionStore, null); }
        await writer.DisposeAsync();
        while (Context(transaction).State == TransactionState.Active) { await Task.Delay(1, token); }
        await using var next = await harness.Client.ConnectAsync(token);
        await next.ExecuteAsync("BEGIN", cancellationToken: token);
        var unknown = await Should.ThrowAsync<GraphClientException>(async () =>
            await next.ExecuteAsync("ROLLBACK TO retained", cancellationToken: token));
        unknown.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        await next.ExecuteAsync("ROLLBACK", cancellationToken: token);
        await using var observer = await database.CreateSessionAsync(token);
        await using var result = (await observer.ExecuteAsync("MATCH (n:Item) RETURN n.n", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        values.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Offline storage outranks the undo fence over both transports (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OfflinePendingUndo_ShouldPreferOfflineRefusal(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: tcp);
        var database = harness.Database;
        var factory = harness.ConnectionFactory;
        await using var transport = await factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family, true);
        var wire = new SavepointWireControl(channel, (ProtocolMessageType)GraphProtocolMessageType.Execute, ProtocolMessageType.Error,
            statement => GraphProtocolExecuteMessage.Create(statement).Encode(),
            frame => ProtocolErrorMessage.Decode(frame.Payload.Span).Message, token);
        await wire.StartAsync("graph");

        (await wire.ExecuteAsync("BEGIN")).ShouldBeTrue();
        (await wire.ExecuteAsync("SAVEPOINT retained")).ShouldBeTrue();
        (await wire.ExecuteAsync("INSERT (:Item {n: 4})")).ShouldBeTrue();
        var session = (GraphDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        var coordinator = Coordinator(database);
        var context = Context(session.CurrentTransaction!);
        using var fault = new SavepointUndoFault(coordinator, context, 3);
        try { (await wire.ExecuteAsync("ROLLBACK TO retained")).ShouldBeFalse(); }
        finally { fault.Disarm(); }
        wire.LastFailure.ShouldNotBeNull().ShouldStartWith("CK04009:", Case.Sensitive);
        TransactionCoordinator.IsPartialUndoPending(context).ShouldBeTrue();

        // Act
        Storage(coordinator).TakeOffline(StorageOfflineCause.VersionPurgeFailures,
            "Injected offline storage with pending undo", new IOException("Offline"));
        (await wire.ExecuteAsync("ROLLBACK TO retained")).ShouldBeFalse();

        // Assert: a complete failure/Ready E exchange, with the lifecycle cause first.
        wire.LastFailure.ShouldNotBeNull().ShouldStartWith("CG06012:", Case.Sensitive);
        session.State.ShouldBe(SessionState.Open);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Session and database close drain an admitted wire control on both transports (#1726)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DisposeAsync_AdmittedWireControl_ShouldDrainBeforeClose(bool tcp, bool closeDatabase)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await GraphClientTestHarness.StartAsync(tcp: tcp);
        var database = harness.Database;
        await using var writer = await harness.Client.ConnectAsync(token);

        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT retained", cancellationToken: token);
        await writer.ExecuteAsync("INSERT (:Item {n: 4})", cancellationToken: token);
        var session = (GraphDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        var coordinator = Coordinator(database);
        var context = Context(session.CurrentTransaction!);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int admitted = 0;
        Observe(coordinator.VersionStore, () =>
        {
            Interlocked.Increment(ref admitted);
            entered.TrySetResult();
            release.Wait(token);
        });
        var undo = writer.ExecuteAsync("ROLLBACK TO retained", cancellationToken: token).AsTask();
        Task? close = null;

        // Act: the physical callback owns admission before either close can claim the end gate.
        try
        {
            await entered.Task.WaitAsync(token);
            close = closeDatabase ? database.DisposeAsync().AsTask() : session.DisposeAsync().AsTask();
            close.IsCompleted.ShouldBeFalse();
            context.State.ShouldBe(TransactionState.Active);
            if (closeDatabase)
            { await Should.ThrowAsync<ObjectDisposedException>(async () => await database.CreateSessionAsync(token)); }
            else { session.State.ShouldBe(SessionState.Closed); }
        }
        finally
        {
            release.Set();
            try { await Record.ExceptionAsync(async () => await undo.WaitAsync(token)); }
            finally
            {
                Observe(coordinator.VersionStore, null);
                if (close is not null) { await close.WaitAsync(token); }
            }
        }

        // Assert
        token.ThrowIfCancellationRequested();
        admitted.ShouldBe(1);
        context.State.ShouldNotBe(TransactionState.Active);
        TransactionCoordinator.IsPartialUndoPending(context).ShouldBeFalse();
        if (!closeDatabase)
        {
            await using var observer = await database.CreateSessionAsync(token);
            await using var result = (await observer.ExecuteAsync("MATCH (n:Item) RETURN n.n", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
            var values = new List<long>();
            await foreach (var row in result.GetRowsAsync(token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
            values.ShouldBeEmpty();
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_storage")]
    private static extern ref Assimalign.Cohesion.Database.Storage.Storage Storage(TransactionCoordinator coordinator);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(GraphDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Context")]
    private static extern TransactionContext Context(GraphDatabaseTransaction transaction);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoBatchApplied")]
    private static extern void Observe(RecordSpaceVersionStore store, Action? observer);

}
