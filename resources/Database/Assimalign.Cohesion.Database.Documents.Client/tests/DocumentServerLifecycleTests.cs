using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentServerLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Server applies authentication before opening the database session")]
    public async Task ConnectAsync_RefusedAuthentication_ShouldReturnTypedCode()
    {
        // Arrange
        var authenticator = new RefusingAuthenticator();
        await using var harness = await DocumentClientTestHarness.StartAsync(configure: options => options.Authenticator = authenticator);

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await harness.Client.ConnectAsync(harness.Token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.AuthenticationFailed);
        authenticator.Calls.ShouldBe(1);
        authenticator.Database.ShouldBe("documents");
        authenticator.Principal.ShouldBe("tester");
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Session limit rejects excess clients without disturbing the active session")]
    public async Task ConnectAsync_SessionLimit_ShouldRejectExcessConnection()
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(configure: options => options.MaxSessions = 1);
        await using var first = await harness.Client.ConnectAsync(harness.Token);
        await using var extra = DocumentClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "documents", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Factory,
        });

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await extra.ConnectAsync(harness.Token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        (await first.GetCollectionsAsync(harness.Token)).ShouldBeEmpty();
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Handshake timeout releases an unauthenticated slot")]
    public async Task Server_HandshakeTimeout_ShouldCloseAndReleaseSlot()
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(configure: options => options.AuthenticationTimeout = TimeSpan.FromMilliseconds(100));
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);

        // Act / Assert
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldBeNull();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        (await connection.GetCollectionsAsync(harness.Token)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Idle timeout removes the authenticated server session")]
    public async Task Server_IdleTimeout_ShouldCloseSession()
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(configure: options => options.IdleTimeout = TimeSpan.FromMilliseconds(100));
        await using var connection = await harness.Client.ConnectAsync(harness.Token);

        // Act: wait for the observable lifecycle transition, not an assumed timer window.
        while (harness.Server.Sessions.Count != 0) { await Task.Delay(10, harness.Token); }

        // Assert
        await Should.ThrowAsync<DocumentClientException>(async () => await connection.GetCollectionsAsync(harness.Token));
        connection.IsOpen.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Stop drains sessions and releases the listener")]
    public async Task StopAsync_ConnectedAndUnauthenticatedPeers_ShouldDrainAllWork()
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await using var stalled = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        while (harness.Server.Sessions.Count < 2) { await Task.Delay(10, harness.Token); }

        // Act
        await harness.Server.StopAsync(harness.Token);

        // Assert
        harness.Server.Sessions.ShouldBeEmpty();
        await Should.ThrowAsync<DocumentClientException>(async () => await connection.GetCollectionsAsync(harness.Token));
        await harness.Server.StopAsync(harness.Token);
    }

}
