using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// An HTTP/2 request body cut off before its END_STREAM faults the reader instead of ending cleanly
/// (#1327, RFC 9113 §8.1): a peer <c>RST_STREAM</c> or the loss of the connection while a read is
/// waiting must never read as the end of the body, with or without a <c>content-length</c> to catch
/// it, while a real END_STREAM still ends it cleanly.
/// </summary>
public class Http2RequestBodyTruncationTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Body: A peer reset during a waiting read should fault the read, not end the body")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadBody_OnPeerResetWhileWaiting_ShouldFault(bool declaresContentLength)
    {
        // Arrange — part of the body arrived and was read; the next read waits for more.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        (IHttpContext context, Task<int> pending) = await StartWaitingReadAsync(peer, declaresContentLength);

        // Act
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);

        // Assert — a fault, and the exchange observes the abort.
        await ShouldFaultAsync(pending);
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Body: Losing the connection during a waiting read should fault the read, not end the body")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadBody_OnConnectionLossWhileWaiting_ShouldFault(bool declaresContentLength)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        (IHttpContext context, Task<int> pending) = await StartWaitingReadAsync(peer, declaresContentLength);

        // Act — the client's side of the connection ends before the body's END_STREAM.
        peer.Transport.CompleteInput();

        // Assert
        await ShouldFaultAsync(pending);
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Body: A read after a peer reset should fault, not end the body")]
    public async Task ReadBody_AfterPeerReset_ShouldFault()
    {
        // Arrange — the reset lands between two reads.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext context = await peer.ReceiveContextAsync();
        await peer.SendDataAsync(1, Http2TestPeer.CreateBody(400), endStream: false);
        await context.Request.Body.ReadExactlyAsync(new byte[400]).AsTask().WaitAsync(_timeout);

        // Act
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);
        await peer.SyncAsync();

        // Assert
        await ShouldFaultAsync(context.Request.Body.ReadAsync(new byte[16]).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Body: The peer's END_STREAM should end the body cleanly")]
    public async Task ReadBody_OnEndStream_ShouldEndCleanly()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        (IHttpContext context, Task<int> pending) = await StartWaitingReadAsync(peer, declaresContentLength: false);

        // Act — the client sends its last DATA frame with END_STREAM.
        await peer.SendDataAsync(1, Http2TestPeer.CreateBody(100), endStream: true);
        int last = await pending.WaitAsync(_timeout);
        int end = await context.Request.Body.ReadAsync(new byte[16]).AsTask().WaitAsync(_timeout);

        // Assert — the remaining octets, then a clean end.
        last.ShouldBe(100);
        end.ShouldBe(0);
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();

        // Answer the exchange so the connection's graceful close has nothing to wait for.
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    // Opens stream 1 with a POST, delivers and reads 400 body octets, then starts a read that waits for
    // more. The PING round trip guarantees the pump processed every frame first, so the read is parked.
    private static async Task<(IHttpContext Context, Task<int> Pending)> StartWaitingReadAsync(Http2TestPeer peer, bool declaresContentLength)
    {
        (string Name, string Value)[] request = declaresContentLength
            ? Http2TestPeer.Request("POST", "/upload", ("content-length", "1000"))
            : Http2TestPeer.Request("POST", "/upload");
        await peer.SendHeadersAsync(1, endStream: false, request);
        IHttpContext context = await peer.ReceiveContextAsync();

        await peer.SendDataAsync(1, Http2TestPeer.CreateBody(400), endStream: false);
        await context.Request.Body.ReadExactlyAsync(new byte[400]).AsTask().WaitAsync(_timeout);

        Task<int> pending = context.Request.Body.ReadAsync(new byte[1000]).AsTask();
        await peer.SyncAsync();
        pending.IsCompleted.ShouldBeFalse();

        return (context, pending);
    }

    // RFC 9113 §8.1 — only END_STREAM completes a request: a cut-off body must surface as the
    // transport's abort (OperationCanceledException) or a stream failure (IOException), never as 0.
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
