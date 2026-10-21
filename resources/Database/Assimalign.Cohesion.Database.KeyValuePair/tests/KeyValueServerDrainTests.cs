using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

[Collection(nameof(KeyValueDatabaseEventSourceCollection))]
public sealed class KeyValueServerDrainTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Soft stop during the final Ready flush still sends its terminal notice")]
    public async Task RunAsync_SoftStopDuringReadyFlush_ShouldDeliverUnavailable()
    {
        // Arrange: Authenticate uses flushes 1/2; Ready is delivered by flush 3, then
        // its explicit flush 4 waits cooperatively for the soft-stop cancellation.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var listener = new BlockingOutputListener(-4);
        await using var engine = KeyValueDatabaseEngine.Create("ready-drain", new());
        await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var server = KeyValueDatabaseServer.Create(engine, new()
        {
            Listener = listener, ShutdownDrainTimeout = TimeSpan.FromSeconds(2),
        });
        await server.StartAsync(timeout.Token);
        await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, timeout.Token);
        var accepted = await listener.Accepted.WaitAsync(timeout.Token);
        await using var channel = new ProtocolChannel(peer.AsStream(), KeyValueProtocol.Family, leaveOpen: true);
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
        await peer.DisposeAsync();
        await stopped;
        accepted.PendingOperations.ShouldBe(0);
        accepted.Disposals.ShouldBe(1);
        server.Sessions.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Terminal errors abort blocked writes, flushes and output completion within one drain budget")]
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
        var margin = TimeSpan.FromMilliseconds(500);
        var log = KeyValueDatabaseEventSource.Log;
        long current = log.CurrentServerSessions;
        long total = log.TotalServerSessions;
        await using var listener = new BlockingOutputListener(blockedFlush);
        await using var engine = KeyValueDatabaseEngine.Create("terminal-drain", new());
        await using var server = KeyValueDatabaseServer.Create(engine, new() { Listener = listener, ShutdownDrainTimeout = drainTimeout });
        await server.StartAsync(token);
        await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, token);
        var connection = await listener.Accepted.WaitAsync(token);
        await using var channel = new ProtocolChannel(peer.AsStream(), KeyValueProtocol.Family, leaveOpen: true);
        KeyValueDatabaseServerSession? session = null;

        try
        {
            // Act: a missing database triggers a terminal shared Error before authentication.
            await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
                new ProtocolStartupMessage(ProtocolVersion.Current, "absent", "tester").Encode()), token);
            await channel.Writer.FlushAsync(token);
            await connection.Blocked.WaitAsync(token);
            session = server.Sessions.ShouldHaveSingleItem().ShouldBeOfType<KeyValueDatabaseServerSession>();
            long started = Stopwatch.GetTimestamp();
            try { await session.Completion.WaitAsync(drainTimeout + margin, token); }
            catch (TimeoutException) { }

            // Assert: completion means teardown joined the I/O and removed the registry entry.
            session.Completion.IsCompleted.ShouldBeTrue($"Blocked flush={blockedFlush}; retained sessions={server.Sessions.Count}; pending I/O={connection.PendingOperations}; aborts={connection.Aborts}.");
            Stopwatch.GetElapsedTime(started).ShouldBeLessThan(drainTimeout + margin);
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Terminal drain retains the session until aborted output completion has unwound")]
    public async Task RunAsync_AbortWithPendingCompletion_ShouldJoinBeforeReleasingSession()
    {
        // Arrange: abort closes the connection, but the output operation has a separate unwind gate.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var log = KeyValueDatabaseEventSource.Log;
        long current = log.CurrentServerSessions;
        long total = log.TotalServerSessions;
        await using var listener = new BlockingOutputListener(0, holdOutputAfterAbort: true);
        await using var engine = KeyValueDatabaseEngine.Create("terminal-join", new());
        await using var server = KeyValueDatabaseServer.Create(engine, new() { Listener = listener, ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100) });
        await server.StartAsync(token);
        await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, token);
        var connection = await listener.Accepted.WaitAsync(token);
        await using var channel = new ProtocolChannel(peer.AsStream(), KeyValueProtocol.Family, leaveOpen: true);

        try
        {
            // Act: wait for the deadline's abort, keeping completion blocked until assertions finish.
            await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
                new ProtocolStartupMessage(ProtocolVersion.Current, "absent", "tester").Encode()), token);
            await channel.Writer.FlushAsync(token);
            await connection.Blocked.WaitAsync(token);
            var session = server.Sessions.ShouldHaveSingleItem().ShouldBeOfType<KeyValueDatabaseServerSession>();
            await connection.Aborted.WaitAsync(TimeSpan.FromMilliseconds(600), token);

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
}
