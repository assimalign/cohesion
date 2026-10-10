using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// A request head that decodes but breaks a field rule (#1332): an uppercase field name (RFC 9113
/// §8.2.1), a connection-specific field or a <c>TE</c> other than <c>trailers</c> (§8.2.2), or a
/// pseudo-header field that is unknown, response-only, or after a regular field (§8.3). The request
/// is malformed, so only its stream is reset with <c>PROTOCOL_ERROR</c> (§8.1.1): it never reaches the
/// application, and the connection keeps serving its other streams. The whole block was decoded
/// before any field was judged, so the HPACK state is intact.
/// </summary>
public class Http2MalformedRequestHeadTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Malformed Heads: A field that breaks a field rule should reset only its stream")]
    [InlineData("User-Agent", "tests")]          // §8.2.1: uppercase name
    [InlineData("connection", "close")]          // §8.2.2: connection-specific
    [InlineData("keep-alive", "timeout=5")]      // §8.2.2: connection-specific
    [InlineData("proxy-connection", "close")]    // §8.2.2: connection-specific
    [InlineData("transfer-encoding", "chunked")] // §8.2.2: connection-specific
    [InlineData("upgrade", "h2c")]               // §8.2.2: connection-specific
    [InlineData("te", "gzip")]                   // §8.2.2: TE other than trailers
    public async Task ReceiveAsync_OnRegularFieldBreakingRule_ShouldResetStreamWithProtocolError(string name, string value)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act
        IHttpContext next = await SendMalformedThenValidAsync(peer, Http2TestPeer.Get("/malformed").Append((name, value)).ToArray());

        // Assert
        await AssertResetBeforeDispatchAsync(peer, next);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Malformed Heads: A pseudo-header that is unknown, response-only or late should reset only its stream")]
    [InlineData(":foo", false)]    // §8.3: not a request pseudo-header
    [InlineData(":status", false)] // §8.3: a response pseudo-header
    [InlineData(":path", true)]    // §8.3: a pseudo-header after a regular field
    public async Task ReceiveAsync_OnPseudoHeaderBreakingRule_ShouldResetStreamWithProtocolError(string name, bool afterRegularField)
    {
        // Arrange — a late :path stands in for the request's own; the others ride a complete head.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        (string Name, string Value)[] fields = afterRegularField
            ? [(":method", "GET"), (":scheme", "https"), (":authority", "api.test"), ("user-agent", "tests"), (name, "/malformed")]
            : [.. Http2TestPeer.Get("/malformed"), (name, "200")];

        // Act
        IHttpContext next = await SendMalformedThenValidAsync(peer, fields);

        // Assert
        await AssertResetBeforeDispatchAsync(peer, next);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Malformed Heads: A malformed head that indexes a field should keep the decoder in step")]
    public async Task ReceiveAsync_OnMalformedHeadThatIndexesField_ShouldDecodeLaterRequestThatReferencesIt()
    {
        // Arrange — the rejected head adds x-kept to the dynamic table, then breaks a field rule.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(
            1,
            endStream: true,
            HPackTestEncoder.Block(
                HPackTestEncoder.RequestHead("GET", "/malformed"),
                HPackTestEncoder.LiteralWithIndexing("x-kept", "decoded"),
                HPackTestEncoder.Literal("Upper", "case"))));

        // Act — the next request references the entry the rejected head added.
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(
            3,
            endStream: true,
            HPackTestEncoder.Block(
                HPackTestEncoder.RequestHead("GET", "/next"),
                HPackTestEncoder.Indexed(HPackTestEncoder.NewestDynamicIndex))));
        IHttpContext next = await peer.ReceiveContextAsync();

        // Assert
        next.Request.Headers[new HttpHeaderKey("x-kept")].Value.ShouldBe("decoded");
        await AssertResetBeforeDispatchAsync(peer, next);
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
