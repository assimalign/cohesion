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
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

[Collection("Savepoint wire failures")]
public sealed class SavepointFailureWireTests
{
    public static IEnumerable<object[]> UndoFailures
    {
        get
        {
            foreach (var fault in SavepointUndoFault.Cases())
            {
                yield return [false, fault[0], (int)fault[1]];
                yield return [true, fault[0], (int)fault[1]];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Every undo stage reports CK04009 and Ready E, then recovers exactly on both transports (#1726)")]
    [MemberData(nameof(UndoFailures))]
    public async Task ExecuteAsync_UndoFailure_ShouldKeepReadyAndRecover(bool tcp, int stage, int recovery)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        var database = harness.Engine.TryGetDatabase("app", out var found) ? found! : throw new InvalidOperationException();
        await using var seed = await database.CreateSessionAsync(token);
        await seed.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, n INT)", cancellationToken: token);
        await seed.ExecuteAsync("INSERT INTO items VALUES (1, 1)", cancellationToken: token);
        var factory = tcp ? (Assimalign.Cohesion.Connections.IDuplexConnectionFactory)new Assimalign.Cohesion.Connections.Tcp.TcpConnectionFactory() : ((Assimalign.Cohesion.Connections.InMemory.InMemoryConnectionListener)harness.Listener).CreateFactory();
        await using var transport = await factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family, true);
        await Send(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "app", "tester")
            { Capabilities = [ProtocolCapability.TransactionStatus.Name, ProtocolCapability.Diagnostics.Name] }.Encode());
        (await Read()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await Send(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        var startup = await Read();
        startup.Type.ShouldBe(ProtocolMessageType.Ready);
        channel.SetAgreement(ProtocolReadyMessage.Decode(startup.Payload.Span));
        await Execute("BEGIN", ProtocolTransactionStatus.Active);
        await Execute("INSERT INTO items VALUES (0, 0)", ProtocolTransactionStatus.Active);
        await Execute("SAVEPOINT outer_scope", ProtocolTransactionStatus.Active);
        await Execute("INSERT INTO items VALUES (2, 2)", ProtocolTransactionStatus.Active);
        await Execute("SAVEPOINT scope", ProtocolTransactionStatus.Active);
        await Execute("INSERT INTO items VALUES (3, 3)", ProtocolTransactionStatus.Active);
        for (int i = 0; i < 100; i++) { await Execute("UPDATE items SET n = n + 1 WHERE id = 3", ProtocolTransactionStatus.Active); }
        await Execute("SAVEPOINT child", ProtocolTransactionStatus.Active);
        var session = (SqlDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        var coordinator = Coordinator(database);
        using var fault = new SavepointUndoFault(coordinator, Context(session.CurrentTransaction!), stage);
        if (stage >= 7)
        {
            for (int i = 0; i < 100; i++) { await Execute("UPDATE items SET n = n + 1 WHERE id = 3", ProtocolTransactionStatus.Active); }
        }

        // Act
        try
        {
            await Send(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("ROLLBACK TO scope").Encode());
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

        var retained = session.CurrentTransaction.ShouldNotBeNull();
        foreach (string refused in new[] { "SELECT n FROM items", "COMMIT", "BEGIN", "SAVEPOINT refused", "RELEASE scope", "ROLLBACK TO child", "INSERT INTO items VALUES (9, 9)", "UPDATE items SET n = 9 WHERE id = 1", "DELETE FROM items WHERE id = 1", "CREATE TABLE refused_table (n INT)", "DROP TABLE items" })
        {
            await Send(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(refused).Encode());
            var rejected = await Read();
            if (rejected.Type == ProtocolMessageType.Diagnostics) { rejected = await Read(); }
            rejected.Type.ShouldBe(ProtocolMessageType.Error);
            ProtocolErrorMessage.Decode(rejected.Payload.Span).Message.ShouldStartWith("CK04009:", Case.Sensitive);
            var fencedReady = await Read();
            fencedReady.Type.ShouldBe(ProtocolMessageType.Ready);
            ProtocolTransactionStatusMessage.Decode(fencedReady.Payload.Span).Status.ShouldBe(ProtocolTransactionStatus.Failed);
            session.State.ShouldBe(SessionState.Open);
            session.CurrentTransaction.ShouldBeSameAs(retained);
            retained.State.ShouldBe(TransactionState.Faulted);
            Context(retained).State.ShouldBe(TransactionState.Active);
        }

        // Assert
        fault.Calls.ShouldBe(fault.ExpectedCalls);
        await Execute(recovery == 2 ? "ROLLBACK" : recovery == 1 ? "ROLLBACK TO outer_scope" : "ROLLBACK TO scope", recovery == 2 ? ProtocolTransactionStatus.Idle : ProtocolTransactionStatus.Active);
        if (stage is 2 or 3 or 9 or 10) { fault.Value.ShouldBe(7); }
        if (stage is < 4 or >= 7) { fault.Calls.ShouldBe(2); }
        if (recovery == 2) { await Execute("BEGIN", ProtocolTransactionStatus.Active); }
        await Execute("INSERT INTO items VALUES (4, 4)", ProtocolTransactionStatus.Active);
        await Execute("COMMIT", ProtocolTransactionStatus.Idle);
        session.State.ShouldBe(SessionState.Open);
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync(token);
        await using var result = (await observer.ExecuteAsync("SELECT n FROM items ORDER BY id", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        values.ShouldBe(recovery == 2 ? new long[] { 1, 4 } : recovery == 1 ? new long[] { 0, 1, 4 } : new long[] { 0, 1, 2, 4 });

        async Task Send(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        { await channel.Writer.WriteFrameAsync(new(type, payload), token); await channel.Writer.FlushAsync(token); }
        async Task<ProtocolFrame> Read() => (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
        async Task Execute(string statement, ProtocolTransactionStatus expectedStatus)
        {
            await Send(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(statement).Encode());
            ProtocolFrame frame;
            do { frame = await Read(); frame.Type.ShouldNotBe(ProtocolMessageType.Error); }
            while (frame.Type != ProtocolMessageType.Ready);
            ProtocolTransactionStatusMessage.Decode(frame.Payload.Span).Status.ShouldBe(expectedStatus);
            var currentSession = (SqlDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
            currentSession.State.ShouldBe(SessionState.Open);
            if (expectedStatus == ProtocolTransactionStatus.Idle) { currentSession.CurrentTransaction.ShouldBeNull(); }
            else
            {
                var current = currentSession.CurrentTransaction.ShouldNotBeNull();
                current.State.ShouldBe(expectedStatus == ProtocolTransactionStatus.Failed ? TransactionState.Faulted : TransactionState.Active);
                Context(current).State.ShouldBe(TransactionState.Active);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - A disposed database with a live fence returns CS04005 and keeps the wire session open (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DisposedFencedDatabase_ShouldKeepCodedReady(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        harness.Engine.TryGetDatabase("app", out var database).ShouldBeTrue();
        await using var seed = await database!.CreateSessionAsync(token);
        await seed.ExecuteAsync("CREATE TABLE items (id INT)", cancellationToken: token);
        var factory = tcp ? (IDuplexConnectionFactory)new Assimalign.Cohesion.Connections.Tcp.TcpConnectionFactory()
            : ((Assimalign.Cohesion.Connections.InMemory.InMemoryConnectionListener)harness.Listener).CreateFactory();
        await using var transport = await factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family, true);
        await Send(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "app", "tester")
            { Capabilities = [ProtocolCapability.TransactionStatus.Name, ProtocolCapability.Diagnostics.Name] }.Encode());
        (await Read()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await Send(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        channel.SetAgreement(ProtocolReadyMessage.Decode((await Read()).Payload.Span));
        await Execute("BEGIN", ProtocolTransactionStatus.Active);
        await Execute("SAVEPOINT scope", ProtocolTransactionStatus.Active);
        await Execute("INSERT INTO items VALUES (1)", ProtocolTransactionStatus.Active);
        var session = (SqlDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        var coordinator = Coordinator(database);
        Observe(coordinator.VersionStore, () => throw new IOException("Injected undo failure"));
        try { await Execute("ROLLBACK TO scope", ProtocolTransactionStatus.Failed); }
        finally { Observe(coordinator.VersionStore, null); }
        TransactionCoordinator.IsPartialUndoPending(Context(session.CurrentTransaction!)).ShouldBeTrue();
        Interlocked.Exchange(ref Disposed(database), 1).ShouldBe(0);

        // Act / Assert: two complete error/Ready exchanges prove the pump stays alive.
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await Send(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT 1").Encode());
                var error = await Read();
                if (error.Type == ProtocolMessageType.Diagnostics) { error = await Read(); }
                error.Type.ShouldBe(ProtocolMessageType.Error);
                ProtocolErrorMessage.Decode(error.Payload.Span).Message.ShouldStartWith("CS04005:", Case.Sensitive);
                var ready = await Read();
                ready.Type.ShouldBe(ProtocolMessageType.Ready);
                ProtocolTransactionStatusMessage.Decode(ready.Payload.Span).Status.ShouldBe(ProtocolTransactionStatus.Failed);
                session.State.ShouldBe(SessionState.Open);
            }
        }
        finally { Volatile.Write(ref Disposed(database), 0); }
        await Execute("ROLLBACK", ProtocolTransactionStatus.Idle);
        session.State.ShouldBe(SessionState.Open);

        async Task Send(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        { await channel.Writer.WriteFrameAsync(new(type, payload), token); await channel.Writer.FlushAsync(token); }
        async Task<ProtocolFrame> Read() => (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
        async Task Execute(string statement, ProtocolTransactionStatus expectedStatus)
        {
            await Send(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(statement).Encode());
            ProtocolFrame frame;
            do { frame = await Read(); } while (frame.Type != ProtocolMessageType.Ready);
            ProtocolTransactionStatusMessage.Decode(frame.Payload.Span).Status.ShouldBe(expectedStatus);
            var currentSession = (SqlDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
            currentSession.State.ShouldBe(SessionState.Open);
            if (expectedStatus == ProtocolTransactionStatus.Idle) { currentSession.CurrentTransaction.ShouldBeNull(); }
            else
            {
                var current = currentSession.CurrentTransaction.ShouldNotBeNull();
                current.State.ShouldBe(expectedStatus == ProtocolTransactionStatus.Failed ? TransactionState.Faulted : TransactionState.Active);
                Context(current).State.ShouldBe(TransactionState.Active);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Returning a fenced connection rolls back all scopes and certifies an exact clean lease (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_PendingUndo_ShouldResetBeforeNextLease(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp, configureSettings: settings => settings.MaxPoolSize = 1);
        var database = harness.Engine.TryGetDatabase("app", out var found) ? found! : throw new InvalidOperationException();
        await using var writer = await harness.Client.ConnectAsync(token);
        await writer.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, n INT)", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items VALUES (1, 1)", cancellationToken: token);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items VALUES (2, 2)", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT pooled", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items VALUES (3, 3)", cancellationToken: token);
        var server = harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null);
        var session = (SqlDatabaseSession)server.DatabaseSession!;
        using var fault = new SavepointUndoFault(Coordinator(database), Context(session.CurrentTransaction!), 2);
        try
        {
            (await Should.ThrowAsync<SqlClientException>(async () => await writer.ExecuteAsync("ROLLBACK TO pooled", cancellationToken: token)))
                .Diagnostics.ShouldContain(item => item.Code == "CK04009");
        }
        finally { fault.Disarm(); }

        // Act
        await writer.DisposeAsync();
        session.CurrentTransaction.ShouldBeNull();
        fault.Value.ShouldBe(7);
        fault.Calls.ShouldBe(2);
        await using var next = await harness.Client.ConnectAsync(token);
        await next.ExecuteAsync("BEGIN", cancellationToken: token);
        harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null).Id.ShouldBe(server.Id);
        var unknown = await Should.ThrowAsync<SqlClientException>(async () => await next.ExecuteAsync("ROLLBACK TO pooled", cancellationToken: token));
        unknown.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        await next.ExecuteAsync("ROLLBACK", cancellationToken: token);
        await next.ExecuteAsync("INSERT INTO items VALUES (4, 4)", cancellationToken: token);

        // Assert
        var rows = await next.QueryAsync("SELECT n FROM items ORDER BY id", cancellationToken: token);
        rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(new long[] { 1, 4 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Repeated connection return, drop or cancellation during admitted undo drains the writer and releases the pool slot (#1726)")]
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
        int rounds = 0, admitted = 0, completed = 0, overlapped = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp, configureSettings: settings => settings.MaxPoolSize = 1);
            var database = harness.Engine.TryGetDatabase("app", out var found) ? found! : throw new InvalidOperationException();
            await using var writer = await harness.Client.ConnectAsync(token);
            await writer.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, n INT)", cancellationToken: token);
            await writer.ExecuteAsync("INSERT INTO items VALUES (1, 1)", cancellationToken: token);
            await writer.ExecuteAsync("BEGIN", cancellationToken: token);
            await writer.ExecuteAsync("INSERT INTO items VALUES (2, 2)", cancellationToken: token);
            await writer.ExecuteAsync("SAVEPOINT target", cancellationToken: token);
            await writer.ExecuteAsync("INSERT INTO items VALUES (3, 3)", cancellationToken: token);
            var session = (SqlDatabaseSession)harness.Server.Sessions.Single(item => item.DatabaseSession!.CurrentTransaction is not null).DatabaseSession!;
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
                var serverSession = harness.Server.Sessions.Single(item => ReferenceEquals(item.DatabaseSession, session));
                var lifetime = Lifetime(serverSession).Token;
                var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = lifetime.Register(() =>
                {
                    if (!release.IsSet) { Interlocked.Increment(ref overlapped); }
                    canceled.TrySetResult();
                });
                returning = ending == 1 ? writer.AbortAsync().AsTask() : writer.DisposeAsync().AsTask();
                await canceled.Task.WaitAsync(token);
                overlapped.ShouldBe(rounds + 1);
                release.IsSet.ShouldBeFalse();
                context.State.ShouldBe(TransactionState.Active);
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
            var rows = await next.QueryAsync("SELECT n FROM items ORDER BY id", cancellationToken: token);
            rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(new long[] { 1 });
            completed++;
            rounds++;
        }
        while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 8 || overlapped < 8)
            && elapsed.Elapsed < TimeSpan.FromSeconds(45));
        rounds.ShouldBeGreaterThanOrEqualTo(8);
        admitted.ShouldBe(rounds);
        completed.ShouldBe(rounds);
        overlapped.ShouldBe(rounds);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Failed savepoint controls keep the ordinary model failure rule over both transports (#1726)")]
    [InlineData(false, "RELEASE absent_scope")]
    [InlineData(true, "RELEASE absent_scope")]
    [InlineData(false, "ROLLBACK TO absent_scope")]
    [InlineData(true, "ROLLBACK TO absent_scope")]
    public async Task ExecuteAsync_UnknownControl_ShouldKeepModelFailureRule(bool tcp, string control)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var writer = await harness.Client.ConnectAsync(token);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT retained", cancellationToken: token);

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await writer.ExecuteAsync(control, cancellationToken: token));

        // Assert
        error.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        await writer.ExecuteAsync("ROLLBACK TO retained", cancellationToken: token);
        await writer.ExecuteAsync("ROLLBACK", cancellationToken: token);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_disposed")]
    private static extern ref int Disposed(DatabaseInstance database);


    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Cancellation before dispatch admits no control and a discarded lease resets every scope (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CanceledBeforeDispatch_ShouldDiscardAndReset(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        var database = harness.Engine.TryGetDatabase("app", out var found) ? found! : throw new InvalidOperationException();
        await using var writer = await harness.Client.ConnectAsync(token);
        await writer.ExecuteAsync("CREATE TABLE items (n INT)", cancellationToken: token);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT retained", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items VALUES (4)", cancellationToken: token);
        var session = (SqlDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
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
        var unknown = await Should.ThrowAsync<SqlClientException>(async () =>
            await next.ExecuteAsync("ROLLBACK TO retained", cancellationToken: token));
        unknown.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        await next.ExecuteAsync("ROLLBACK", cancellationToken: token);
        await using var observer = await database.CreateSessionAsync(token);
        await using var result = (await observer.ExecuteAsync("SELECT n FROM items", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        values.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Offline storage outranks the undo fence over both transports (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OfflinePendingUndo_ShouldPreferOfflineRefusal(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        var database = harness.Engine.TryGetDatabase("app", out var found) ? found! : throw new InvalidOperationException();
        var factory = tcp ? (IDuplexConnectionFactory)new Assimalign.Cohesion.Connections.Tcp.TcpConnectionFactory() : ((Assimalign.Cohesion.Connections.InMemory.InMemoryConnectionListener)harness.Listener).CreateFactory();
        await using var transport = await factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family, true);
        var wire = new SavepointWireControl(channel, ProtocolMessageType.Execute, ProtocolMessageType.Error,
            statement => ProtocolExecuteMessage.Create(statement).Encode(),
            frame => ProtocolErrorMessage.Decode(frame.Payload.Span).Message, token);
        await wire.StartAsync("app");
        (await wire.ExecuteAsync("CREATE TABLE items (n INT)", ProtocolTransactionStatus.Idle)).ShouldBeTrue();
        (await wire.ExecuteAsync("BEGIN", ProtocolTransactionStatus.Active)).ShouldBeTrue();
        (await wire.ExecuteAsync("SAVEPOINT retained", ProtocolTransactionStatus.Active)).ShouldBeTrue();
        (await wire.ExecuteAsync("INSERT INTO items VALUES (4)", ProtocolTransactionStatus.Active)).ShouldBeTrue();
        var session = (SqlDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        var coordinator = Coordinator(database);
        var context = Context(session.CurrentTransaction!);
        using var fault = new SavepointUndoFault(coordinator, context, 3);
        try { (await wire.ExecuteAsync("ROLLBACK TO retained", ProtocolTransactionStatus.Failed)).ShouldBeFalse(); }
        finally { fault.Disarm(); }
        wire.LastFailure.ShouldNotBeNull().ShouldStartWith("CK04009:", Case.Sensitive);
        TransactionCoordinator.IsPartialUndoPending(context).ShouldBeTrue();

        // Act
        Storage(coordinator).TakeOffline(StorageOfflineCause.VersionPurgeFailures,
            "Injected offline storage with pending undo", new IOException("Offline"));
        (await wire.ExecuteAsync("ROLLBACK TO retained", ProtocolTransactionStatus.Failed)).ShouldBeFalse();

        // Assert: a complete failure/Ready E exchange, with the lifecycle cause first.
        wire.LastFailure.ShouldNotBeNull().ShouldStartWith("CS04004:", Case.Sensitive);
        session.State.ShouldBe(SessionState.Open);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Corruption during undo aborts the writer over both transports (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CorruptUndo_ShouldAbortWriter(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        var database = harness.Engine.TryGetDatabase("app", out var found) ? found! : throw new InvalidOperationException();
        var factory = tcp ? (IDuplexConnectionFactory)new Assimalign.Cohesion.Connections.Tcp.TcpConnectionFactory() : ((Assimalign.Cohesion.Connections.InMemory.InMemoryConnectionListener)harness.Listener).CreateFactory();
        await using var transport = await factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family, true);
        var wire = new SavepointWireControl(channel, ProtocolMessageType.Execute, ProtocolMessageType.Error,
            statement => ProtocolExecuteMessage.Create(statement).Encode(),
            frame => ProtocolErrorMessage.Decode(frame.Payload.Span).Message, token);
        await wire.StartAsync("app");
        (await wire.ExecuteAsync("CREATE TABLE items (n INT)", ProtocolTransactionStatus.Idle)).ShouldBeTrue();
        (await wire.ExecuteAsync("BEGIN", ProtocolTransactionStatus.Active)).ShouldBeTrue();
        (await wire.ExecuteAsync("SAVEPOINT retained", ProtocolTransactionStatus.Active)).ShouldBeTrue();
        (await wire.ExecuteAsync("INSERT INTO items VALUES (4)", ProtocolTransactionStatus.Active)).ShouldBeTrue();
        var session = (SqlDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        var coordinator = Coordinator(database);
        var context = Context(session.CurrentTransaction!);
        int visits = 0;
        Observe(coordinator.VersionStore, () => { visits++; throw new StorageCorruptionException((PageId)42, "Injected corrupt undo"); });

        // Act
        try { (await wire.ExecuteAsync("ROLLBACK TO retained", ProtocolTransactionStatus.Failed)).ShouldBeFalse(); }
        finally { Observe(coordinator.VersionStore, null); }

        // Assert
        visits.ShouldBe(1);
        wire.LastFailure.ShouldNotBeNull().ShouldStartWith("CK06003:", Case.Sensitive);
        context.State.ShouldNotBe(TransactionState.Active);
        TransactionCoordinator.IsPartialUndoPending(context).ShouldBeFalse();
        session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Faulted);
        (await wire.ExecuteAsync("ROLLBACK", ProtocolTransactionStatus.Idle)).ShouldBeTrue();
        session.State.ShouldBe(SessionState.Open);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Session and database close drain an admitted wire control on both transports (#1726)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DisposeAsync_AdmittedWireControl_ShouldDrainBeforeClose(bool tcp, bool closeDatabase)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        var database = harness.Engine.TryGetDatabase("app", out var found) ? found! : throw new InvalidOperationException();
        await using var writer = await harness.Client.ConnectAsync(token);
        await writer.ExecuteAsync("CREATE TABLE items (n INT)", cancellationToken: token);
        await writer.ExecuteAsync("BEGIN", cancellationToken: token);
        await writer.ExecuteAsync("SAVEPOINT retained", cancellationToken: token);
        await writer.ExecuteAsync("INSERT INTO items VALUES (4)", cancellationToken: token);
        var session = (SqlDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
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
            await using var result = (await observer.ExecuteAsync("SELECT n FROM items", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
            var values = new List<long>();
            await foreach (var row in result.GetRowsAsync(token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
            values.ShouldBeEmpty();
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_storage")]
    private static extern ref Assimalign.Cohesion.Database.Storage.Storage Storage(TransactionCoordinator coordinator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lifetimeSource")]
    private static extern ref CancellationTokenSource Lifetime(
        [UnsafeAccessorType("Assimalign.Cohesion.Database.Sql.Internal.SqlDatabaseServerSession, Assimalign.Cohesion.Database.Sql")] object session);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(SqlDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Context")]
    private static extern TransactionContext Context(SqlDatabaseTransaction transaction);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoBatchApplied")]
    private static extern void Observe(RecordSpaceVersionStore store, Action? observer);

}
