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
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

[Collection("Savepoint wire failures")]
public sealed class SavepointFailureWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Failed undo reports CK04009 and Ready E, then recovers exactly on both transports (#1726)")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ExecuteAsync_UndoFailure_ShouldKeepReadyAndRecover(bool tcp, bool adapter, bool wholeRollback)
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
        await Execute("BEGIN");
        await Execute("INSERT INTO items VALUES (2, 2)");
        await Execute("SAVEPOINT scope");
        await Execute("INSERT INTO items VALUES (3, 3)");
        for (int i = 0; i < 78; i++) { await Execute("UPDATE items SET n = n + 1 WHERE id = 3"); }
        var session = (SqlDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        var coordinator = Coordinator(database);
        var undo = new FailingUndo();
        if (adapter) { coordinator.VersionStore.RecordIndexEntryCreated(Context(session.CurrentTransaction!).Sequence, undo, ReadOnlyMemory<byte>.Empty, 0); }
        int batches = 0;
        if (!adapter) { Observe(coordinator.VersionStore, () => { if (++batches == 2) { throw undo.Failure; } }); }

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
        finally { Observe(coordinator.VersionStore, null); undo.Fail = false; }

        // Assert
        (adapter ? undo.Calls : batches).ShouldBe(adapter ? 1 : 2);
        await Execute(wholeRollback ? "ROLLBACK" : "ROLLBACK TO scope");
        if (wholeRollback) { await Execute("BEGIN"); }
        await Execute("INSERT INTO items VALUES (4, 4)");
        await Execute("COMMIT");
        session.State.ShouldBe(SessionState.Open);
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync(token);
        await using var result = (await observer.ExecuteAsync("SELECT n FROM items ORDER BY id", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        values.ShouldBe(wholeRollback ? new long[] { 1, 4 } : new long[] { 1, 2, 4 });

        async Task Send(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        { await channel.Writer.WriteFrameAsync(new(type, payload), token); await channel.Writer.FlushAsync(token); }
        async Task<ProtocolFrame> Read() => (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
        async Task Execute(string statement)
        {
            await Send(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(statement).Encode());
            ProtocolFrame frame;
            do { frame = await Read(); frame.Type.ShouldNotBe(ProtocolMessageType.Error); }
            while (frame.Type != ProtocolMessageType.Ready);
            ProtocolTransactionStatusMessage.Decode(frame.Payload.Span).Status.ShouldNotBe(ProtocolTransactionStatus.Failed);
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
        await Execute("BEGIN");
        await Execute("SAVEPOINT scope");
        await Execute("INSERT INTO items VALUES (1)");
        var session = (SqlDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        var coordinator = Coordinator(database);
        Observe(coordinator.VersionStore, () => throw new IOException("Injected undo failure"));
        try { await Execute("ROLLBACK TO scope"); }
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
        await Execute("ROLLBACK");
        session.State.ShouldBe(SessionState.Open);

        async Task Send(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        { await channel.Writer.WriteFrameAsync(new(type, payload), token); await channel.Writer.FlushAsync(token); }
        async Task<ProtocolFrame> Read() => (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
        async Task Execute(string statement)
        {
            await Send(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(statement).Encode());
            ProtocolFrame frame;
            do { frame = await Read(); } while (frame.Type != ProtocolMessageType.Ready);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_disposed")]
    private static extern ref int Disposed(DatabaseInstance database);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(SqlDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Context")]
    private static extern TransactionContext Context(SqlDatabaseTransaction transaction);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoBatchApplied")]
    private static extern void Observe(RecordSpaceVersionStore store, Action? observer);

    private sealed class FailingUndo : RecordVersionIndex
    {
        internal readonly IOException Failure = new("Injected recorded undo failure");
        internal bool Fail = true;
        internal int Calls;
        protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong reference, TransactionSequence writer, CancellationToken token)
        { Calls++; if (Fail) { throw Failure; } return default; }
        protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong reference, TransactionSequence writer, CancellationToken token) => default;
    }
}
