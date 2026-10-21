using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphDatabaseEventSourceCollection))]
public sealed class GraphServerDrainTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Soft stop during the final Ready flush still sends its terminal notice")]
    public async Task RunAsync_SoftStopDuringReadyFlush_ShouldDeliverUnavailable()
    {
        // Arrange: Authenticate uses flushes 1/2; Ready is delivered by flush 3, then
        // its explicit flush 4 waits cooperatively for the soft-stop cancellation.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var listener = new BlockingOutputListener(-4);
        await using var engine = GraphDatabaseEngine.Create("ready-drain", new());
        await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var server = GraphDatabaseServer.Create(engine, new()
        {
            Listener = listener, ShutdownDrainTimeout = TimeSpan.FromSeconds(2),
        });
        await server.StartAsync(timeout.Token);
        await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, timeout.Token);
        var accepted = await listener.Accepted.WaitAsync(timeout.Token);
        await using var channel = new ProtocolChannel(peer.AsStream(), GraphProtocol.Family, leaveOpen: true);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
            new ProtocolStartupMessage(ProtocolVersion.Current, "db", "tester").Encode()), timeout.Token);
        (await channel.Reader.ReadFrameAsync(timeout.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty), timeout.Token);
        (await channel.Reader.ReadFrameAsync(timeout.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        await accepted.Blocked.WaitAsync(timeout.Token);

        // Act
        Task stopped = server.StopAsync(timeout.Token);
        var terminal = (await channel.Reader.ReadFrameAsync(timeout.Token)).ShouldNotBeNull();

        // Assert
        terminal.Type.ShouldBe(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(terminal.Payload.Span).Code.ShouldBe(ProtocolErrorCode.Unavailable);
        await stopped;
        accepted.PendingOperations.ShouldBe(0);
        accepted.Disposals.ShouldBe(1);
        server.Sessions.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Terminal errors abort blocked writes, flushes and output completion within one drain budget")]
    [InlineData(1)] // The complete frame write flushes the pipe.
    [InlineData(2)] // The explicit flush after the complete frame write.
    [InlineData(0)] // Output completion has no cancellation parameter.
    [InlineData(-1)] // A cooperative write can unwind inline inside token cancellation.
    public async Task RunAsync_BlockedTerminalOutput_ShouldAbortAndReleaseSession(int blockedFlush)
    {
        // Arrange: exercise both token-aware writes and I/O released only by abort.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var drainTimeout = TimeSpan.FromMilliseconds(100);
        var log = GraphDatabaseEventSource.Log;
        long current = log.CurrentServerSessions;
        long total = log.TotalServerSessions;
        await using var listener = new BlockingOutputListener(blockedFlush);
        await using var engine = GraphDatabaseEngine.Create("terminal-drain", new());
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener, ShutdownDrainTimeout = drainTimeout });
        await server.StartAsync(token);
        await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, token);
        var connection = await listener.Accepted.WaitAsync(token);
        await using var channel = new ProtocolChannel(peer.AsStream(), GraphProtocol.Family, leaveOpen: true);
        GraphDatabaseServerSession? session = null;

        try
        {
            // Act: a missing database triggers a terminal shared Error before authentication.
            await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
                new ProtocolStartupMessage(ProtocolVersion.Current, "absent", "tester").Encode()), token);
            await channel.Writer.FlushAsync(token);
            await connection.Blocked.WaitAsync(token);
            session = server.Sessions.ShouldHaveSingleItem().ShouldBeOfType<GraphDatabaseServerSession>();
            await connection.Aborted.WaitAsync(token);
            await session.Completion.WaitAsync(token);

            // Assert: the deadline aborted I/O before teardown released its registry entry.
            session.Completion.IsCompletedSuccessfully.ShouldBeTrue();
            connection.Aborts.ShouldBe(1);
            connection.Disposals.ShouldBe(1);
            connection.PendingOperations.ShouldBe(0);
            connection.Flushes.ShouldBe(blockedFlush == 0 ? 2 : Math.Abs(blockedFlush));
            connection.Completions.ShouldBe(blockedFlush == 0 ? 1 : 0);
            connection.FlushTokenCanBeCanceled.ShouldBeTrue();
            connection.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
            server.Sessions.ShouldBeEmpty();
            log.CurrentServerSessions.ShouldBe(current);
            log.TotalServerSessions.ShouldBe(total + 1);
        }
        finally
        {
            // Also releases the old implementation when the regression fails.
            connection.ReleaseOutput();
            connection.Abort();
            await server.StopAsync(token);
            if (session is not null) { await session.Completion.WaitAsync(token); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Terminal drain retains the session until aborted output completion has unwound")]
    public async Task RunAsync_AbortWithPendingCompletion_ShouldJoinBeforeReleasingSession()
    {
        // Arrange: abort closes the connection, but the output operation has a separate unwind gate.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var log = GraphDatabaseEventSource.Log;
        long current = log.CurrentServerSessions;
        long total = log.TotalServerSessions;
        await using var listener = new BlockingOutputListener(0, holdOutputAfterAbort: true);
        await using var engine = GraphDatabaseEngine.Create("terminal-join", new());
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener, ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100) });
        await server.StartAsync(token);
        await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, token);
        var connection = await listener.Accepted.WaitAsync(token);
        await using var channel = new ProtocolChannel(peer.AsStream(), GraphProtocol.Family, leaveOpen: true);

        try
        {
            // Act: wait for the deadline's abort, keeping completion blocked until assertions finish.
            await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
                new ProtocolStartupMessage(ProtocolVersion.Current, "absent", "tester").Encode()), token);
            await channel.Writer.FlushAsync(token);
            await connection.Blocked.WaitAsync(token);
            var session = server.Sessions.ShouldHaveSingleItem().ShouldBeOfType<GraphDatabaseServerSession>();
            await connection.Aborted.WaitAsync(token);

            // Assert: no abandoned completion task or premature session/counter release.
            session.Completion.IsCompleted.ShouldBeFalse();
            connection.PendingOperations.ShouldBe(1);
            connection.Completions.ShouldBe(1);
            connection.Aborts.ShouldBe(1);
            connection.Disposals.ShouldBe(0);
            server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(session);
            log.CurrentServerSessions.ShouldBe(current + 1);
            connection.ReleaseOutput();
            await session.Completion.WaitAsync(token);
            connection.PendingOperations.ShouldBe(0);
            connection.Disposals.ShouldBe(1);
            server.Sessions.ShouldBeEmpty();
            log.CurrentServerSessions.ShouldBe(current);
            log.TotalServerSessions.ShouldBe(total + 1);
        }
        finally
        {
            connection.ReleaseOutput();
            connection.Abort();
            await server.StopAsync(token);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - An unread idle peer releases its session before the outer drain deadline")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_UnreadIdlePeer_ShouldCompleteWithoutOuterAbort(bool shutdown)
    {
        // Arrange: the peer stays open and does not consume the terminal notice.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var listener = new BlockingOutputListener(int.MaxValue);
        await using var engine = GraphDatabaseEngine.Create("idle-linger", new());
        await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var server = GraphDatabaseServer.Create(engine, new()
        {
            Listener = listener, MaxSessions = 1,
            IdleTimeout = shutdown ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(100),
            ShutdownDrainTimeout = TimeSpan.FromDays(1),
        });
        await server.StartAsync(timeout.Token);
        await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, timeout.Token);
        var accepted = await listener.Accepted.WaitAsync(timeout.Token);
        await using var channel = new ProtocolChannel(peer.AsStream(), GraphProtocol.Family, leaveOpen: true);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
            new ProtocolStartupMessage(ProtocolVersion.Current, "db", "tester").Encode()), timeout.Token);
        (await channel.Reader.ReadFrameAsync(timeout.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty), timeout.Token);
        (await channel.Reader.ReadFrameAsync(timeout.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        var session = server.Sessions.ShouldHaveSingleItem().ShouldBeOfType<GraphDatabaseServerSession>();

        try
        {
            // Act: the safety token is the only test wait limit.
            if (shutdown) { await server.StopAsync(timeout.Token).WaitAsync(timeout.Token); }
            else { await session.Completion.WaitAsync(timeout.Token); }

            // Assert: neither the peer nor the outer budget ended this wait.
            timeout.IsCancellationRequested.ShouldBeFalse();
            accepted.Aborts.ShouldBe(0);
            accepted.Completions.ShouldBe(1);
            accepted.Disposals.ShouldBe(1);
            server.Sessions.ShouldBeEmpty();
            if (!shutdown)
            {
                await using var replacement = await listener.Factory.ConnectAsync(listener.EndPoint, timeout.Token);
                await using var replacementChannel = new ProtocolChannel(replacement.AsStream(), GraphProtocol.Family, leaveOpen: true);
                await replacementChannel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
                    new ProtocolStartupMessage(ProtocolVersion.Current, "db", "tester").Encode()), timeout.Token);
                (await replacementChannel.Reader.ReadFrameAsync(timeout.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
            }
        }
        finally
        {
            accepted.Abort();
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - A disposed drain reader is a closed transport")]
    public async Task RunAsync_DisposedDrainReader_ShouldCloseWithoutCleanupWarning()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Warning);
        await using var listener = new BlockingOutputListener(int.MaxValue);
        await using var engine = GraphDatabaseEngine.Create("disposed-drain", new());
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(timeout.Token);
        await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, timeout.Token);
        var accepted = await listener.Accepted.WaitAsync(timeout.Token);
        accepted.CloseInputOnDrain = true;
        await using var channel = new ProtocolChannel(peer.AsStream(), GraphProtocol.Family, leaveOpen: true);
        while (server.Sessions.Count == 0) { await Task.Delay(1, timeout.Token); }
        var session = server.Sessions.ShouldHaveSingleItem().ShouldBeOfType<GraphDatabaseServerSession>();

        // Act
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
            new ProtocolStartupMessage(ProtocolVersion.Current, "absent", "tester").Encode()), timeout.Token);
        await session.Completion.WaitAsync(timeout.Token);

        // Assert
        accepted.Completions.ShouldBe(1);
        accepted.Disposals.ShouldBe(1);
        recorder.Events.Where(item => item.EventName == "SessionCleanupFailed").ShouldBeEmpty();
        server.Sessions.ShouldBeEmpty();
    }
}
