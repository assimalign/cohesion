using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentDatabaseEventSourceCollection))]
public sealed class DocumentServerRejectionDrainTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Capacity refusal bounds blocked terminal I/O and joins cleanup")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RejectAsync_BlockedOutput_ShouldAbortAndJoinBeforeStop(int blockedFlush)
    {
        // Arrange: the first unauthenticated peer occupies the only session slot.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var listener = new BlockingOutputListener(blockedFlush);
        await using var engine = DocumentDatabaseEngine.Create("rejection-drain", new());
        await using var server = DocumentDatabaseServer.Create(engine, new()
        {
            Listener = listener, MaxSessions = 1, ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100),
        });
        await server.StartAsync(timeout.Token);
        await using var holder = await listener.Factory.ConnectAsync(listener.EndPoint, timeout.Token);
        await listener.Accepted.WaitAsync(timeout.Token);
        // Registry insertion follows AcceptCoreAsync's return; wait for actual admission.
        while (server.Sessions.Count != 1) { await Task.Delay(1, timeout.Token); }
        await using var peer = await listener.Factory.ConnectAsync(listener.EndPoint, timeout.Token);
        var rejected = await listener.SecondAccepted.WaitAsync(timeout.Token);

        try
        {
            // Act: no Startup is required for the admission refusal.
            await rejected.Blocked.WaitAsync(timeout.Token);
            await rejected.Aborted.WaitAsync(TimeSpan.FromSeconds(1), timeout.Token);
            await server.StopAsync(timeout.Token);

            // Assert: rejection owns no session, and shutdown joins its pending I/O/disposal.
            rejected.Aborts.ShouldBe(1);
            rejected.PendingOperations.ShouldBe(0);
            rejected.Disposals.ShouldBe(1);
            rejected.Completions.ShouldBe(blockedFlush == 0 ? 1 : 0);
            rejected.Flushes.ShouldBe(blockedFlush == 0 ? 2 : Math.Abs(blockedFlush));
            server.Sessions.ShouldBeEmpty();
        }
        finally
        {
            rejected.ReleaseOutput();
            rejected.Abort();
            await server.StopAsync(timeout.Token);
        }
    }
}
