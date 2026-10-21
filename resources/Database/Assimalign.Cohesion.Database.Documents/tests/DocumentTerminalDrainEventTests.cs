using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Protocol;


namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentDatabaseEventSourceCollection))]
public sealed class DocumentTerminalDrainEventTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Terminal deadline abort writes a bounded warning and counts every expiry")]
    public async Task RunAsync_TerminalDeadline_ShouldReportWarningAndExactCounter()
    {
        // Arrange: a preceding expiry may have used the provider's one-second warning slot.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var log = DocumentDatabaseEventSource.Log;
        long initial = log.TotalTerminalDrainTimeouts;
        using var recorder = new EventSourceRecorder(log, EventLevel.Warning, TimeSpan.FromMilliseconds(100));
        await using var engine = DocumentDatabaseEngine.Create("deadline-event", new());
        int expired = 0;

        // Act: drive real blocked writes until a warning is observable, within the safety token.
        do
        {
            await using var listener = new BlockingOutputListener(1);
            await using var server = DocumentDatabaseServer.Create(engine, new()
            {
                Listener = listener, ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100),
            });
            await server.StartAsync(timeout.Token);
            await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, timeout.Token);
            var connection = await listener.Accepted.WaitAsync(timeout.Token);
            await using var channel = new ProtocolChannel(peer.AsStream(), DocumentProtocol.Family, leaveOpen: true);
            await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
                new ProtocolStartupMessage(ProtocolVersion.Current, "absent", "tester").Encode()), timeout.Token);
            await connection.Blocked.WaitAsync(timeout.Token);
            await connection.Aborted.WaitAsync(timeout.Token);
            await server.StopAsync(timeout.Token);
            connection.Disposals.ShouldBe(1);
            expired++;
        }
        while (!recorder.Events.Any(item => item.EventName == "TerminalDrainTimedOut"));

        // Assert
        log.TotalTerminalDrainTimeouts.ShouldBe(initial + expired);
        var warning = recorder.Events.First(item => item.EventName == "TerminalDrainTimedOut");
        warning.Level.ShouldBe(EventLevel.Warning);
        warning.PayloadNames.ShouldNotBeNull().ShouldBe(new[] { "sessionId", "drainTimeoutMilliseconds", "suppressedCount" });
        warning.Payload.ShouldNotBeNull()[1].ShouldBe(100d);
        recorder.Events.Where(item => item.EventId == 0).ShouldBeEmpty();
        await recorder.WaitForCountersAsync(new[] { "total-terminal-drain-timeouts" }, timeout.Token);
    }
}