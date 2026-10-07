using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The pseudo-header fields of an HTTP/2 request (#1321, RFC 9113 §8.3.1): a request that is not a
/// CONNECT carries <c>:method</c>, <c>:scheme</c> and <c>:path</c>, none of them repeated and the
/// <c>:path</c> never empty. A request that breaks the rule is malformed: its stream is reset with
/// <c>PROTOCOL_ERROR</c> before it reaches the application (RFC 9113 §8.1.1), nothing is defaulted, and
/// the connection keeps serving its other streams. CONNECT keeps its own rules (RFC 9113 §8.5,
/// RFC 8441 §4).
/// </summary>
public class Http2RequestPseudoHeaderTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: A request missing a required pseudo-header should reset only its stream")]
    [InlineData(":method")]
    [InlineData(":scheme")]
    [InlineData(":path")]
    public async Task ReceiveAsync_OnRequestMissingPseudoHeader_ShouldResetStreamWithProtocolError(string missing)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        (string Name, string Value)[] fields = Http2TestPeer.Get("/malformed")
            .Where(field => field.Name != missing)
            .ToArray();

        // Act
        IHttpContext next = await SendMalformedThenValidAsync(peer, fields);

        // Assert
        await AssertResetBeforeDispatchAsync(peer, next);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: A request with an empty :path should reset only its stream")]
    [InlineData("GET")]
    [InlineData("OPTIONS")]
    public async Task ReceiveAsync_OnEmptyPath_ShouldResetStreamWithProtocolError(string method)
    {
        // Arrange — an empty :path is never valid; OPTIONS uses "*" for the asterisk form.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act
        IHttpContext next = await SendMalformedThenValidAsync(peer, Http2TestPeer.Request(method, string.Empty));

        // Assert
        await AssertResetBeforeDispatchAsync(peer, next);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: A request that repeats a pseudo-header should reset only its stream")]
    [InlineData(":method", "POST")]
    [InlineData(":scheme", "http")]
    [InlineData(":path", "/other")]
    [InlineData(":authority", "other.test")]
    public async Task ReceiveAsync_OnRepeatedPseudoHeader_ShouldResetStreamWithProtocolError(string name, string value)
    {
        // Arrange — the repeat sits among the pseudo-header fields, ahead of every regular field.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        (string Name, string Value)[] fields = [.. Http2TestPeer.Get("/malformed"), (name, value), ("x-after", "kept")];

        // Act
        IHttpContext next = await SendMalformedThenValidAsync(peer, fields);

        // Assert
        await AssertResetBeforeDispatchAsync(peer, next);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: An OPTIONS request in asterisk form should be dispatched")]
    public async Task ReceiveAsync_OnAsteriskFormOptions_ShouldDispatchRequest()
    {
        // Arrange — RFC 9113 §8.3.1: OPTIONS for a server as a whole carries the :path "*".
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("OPTIONS", "*"));
        IHttpContext options = await peer.ReceiveContextAsync();

        // Assert
        options.Request.Method.ShouldBe(HttpMethod.Options);
        options.Request.Path.Value.ShouldBe("*");

        await peer.ConnectionContext.SendAsync(options).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: A classic CONNECT without :scheme and :path should be dispatched")]
    public async Task ReceiveAsync_OnClassicConnectWithoutSchemeAndPath_ShouldDispatchRequest()
    {
        // Arrange — RFC 9113 §8.5: a CONNECT carries only :method and :authority.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act
        await peer.SendHeadersAsync(1, endStream: false, (":method", "CONNECT"), (":authority", "upstream.test:443"));
        IHttpContext tunnel = await peer.ReceiveContextAsync();

        // Assert
        tunnel.Request.Method.ShouldBe(HttpMethod.Connect);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsRstStream || frame.IsGoAway);

        await peer.SendDataAsync(1, Array.Empty<byte>(), endStream: true);
        await peer.ConnectionContext.SendAsync(tunnel).AsTask().WaitAsync(_timeout);
    }

    /// <summary>
    /// Sends <paramref name="malformed"/> as the head of stream 1, then a valid <c>GET /next</c> on
    /// stream 3, and returns the first exchange the server dispatches.
    /// </summary>
    private static async Task<IHttpContext> SendMalformedThenValidAsync(Http2TestPeer peer, (string Name, string Value)[] malformed)
    {
        await peer.SendHeadersAsync(1, endStream: true, malformed);
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        return await peer.ReceiveContextAsync();
    }

    /// <summary>
    /// Asserts that stream 1 was reset with <c>PROTOCOL_ERROR</c> without reaching the application —
    /// the first exchange dispatched is stream 3's — and that the connection stayed open to serve it.
    /// </summary>
    private static async Task AssertResetBeforeDispatchAsync(Http2TestPeer peer, IHttpContext next)
    {
        next.Request.Path.Value.ShouldBe("/next");
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsHeaders);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);

        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }
}
