using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// An HTTP/3 request body cut off before its FIN faults the reader instead of ending cleanly (#1327,
/// RFC 9114 §4.1): the client resetting its side of the request stream, or the connection going away,
/// while a read is waiting must never read as the end of the body, with or without a
/// <c>content-length</c>, while the client's FIN still ends it cleanly. Driven over the in-memory
/// multiplexed driver (<see cref="Http3InMemoryPeer"/>), where completing the client's output with an
/// error is the reset of its sending direction.
/// </summary>
public class Http3RequestBodyTruncationTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Body: A client reset during a waiting read should fault the read, not end the body")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadBody_OnClientResetWhileWaiting_ShouldFault(bool declaresContentLength)
    {
        // Arrange — part of the body arrived and was read; the next read waits for more.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        (Connection request, _, Task<int> pending) = await StartWaitingReadAsync(peer, declaresContentLength);

        // Act — RESET_STREAM: the client abandons its sending direction.
        request.Output.Complete(new ConnectionAbortedException("The client reset its request stream."));

        // Assert
        await ShouldFaultAsync(pending);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Body: Losing the connection during a waiting read should fault the read, not end the body")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadBody_OnConnectionLossWhileWaiting_ShouldFault(bool declaresContentLength)
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        (_, _, Task<int> pending) = await StartWaitingReadAsync(peer, declaresContentLength);
        await peer.StopReceivingAsync();

        // Act — the QUIC connection goes away underneath the read.
        await peer.Server.DisposeAsync();

        // Assert
        await ShouldFaultAsync(pending);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Body: The client's FIN should end the body cleanly")]
    public async Task ReadBody_OnFin_ShouldEndCleanly()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        (Connection request, IHttpContext context, Task<int> pending) = await StartWaitingReadAsync(peer, declaresContentLength: false);

        // Act — the last DATA frame, then the FIN.
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Http2TestPeer.CreateBody(100)));
        request.Output.Complete();
        int last = await pending.WaitAsync(_timeout);
        int end = await context.Request.Body.ReadAsync(new byte[16]).AsTask().WaitAsync(_timeout);

        // Assert
        last.ShouldBe(100);
        end.ShouldBe(0);
    }

    // Opens a request stream with a POST, delivers and reads 400 body octets, then starts a read that
    // waits for more.
    private static async Task<(Connection Request, IHttpContext Context, Task<int> Pending)> StartWaitingReadAsync(Http3InMemoryPeer peer, bool declaresContentLength)
    {
        Dictionary<string, string>? headers = declaresContentLength
            ? new Dictionary<string, string> { ["content-length"] = "1000" }
            : null;

        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a", headers));
        IHttpContext context = await peer.NextContextAsync();

        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Http2TestPeer.CreateBody(400)));
        await context.Request.Body.ReadExactlyAsync(new byte[400]).AsTask().WaitAsync(_timeout);

        Task<int> pending = context.Request.Body.ReadAsync(new byte[1000]).AsTask();
        await Task.Yield();
        pending.IsCompleted.ShouldBeFalse();

        return (request, context, pending);
    }

    // RFC 9114 §4.1 — only the FIN completes a request: a cut-off body must surface as an IOException
    // (or the exchange's cancellation), never as 0.
    private static async Task ShouldFaultAsync(Task<int> read)
    {
        int octets;

        try
        {
            octets = await read.WaitAsync(_timeout);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return;
        }

        throw new ShouldAssertException($"The cut-off request body read returned {octets} octets instead of faulting.");
    }
}
