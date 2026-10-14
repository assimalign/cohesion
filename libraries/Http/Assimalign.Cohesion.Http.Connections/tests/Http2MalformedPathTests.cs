using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies that a malformed <c>:path</c> on HTTP/2 is a per-stream error (issue #937, RFC 9113
/// §8.1.1 / §8.3.1): the offending stream is reset with <c>PROTOCOL_ERROR</c>, and the connection —
/// with no GOAWAY — keeps serving a sibling stream. The percent-decode itself is the shared
/// <c>HttpPath.FromUriComponent</c>, unchanged, so HTTP/1.1 rejects the same targets.
/// </summary>
public class Http2MalformedPathTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Malformed :path: A :path that is not a legal path should reset only its stream with PROTOCOL_ERROR")]
    [InlineData("/space%20name")]          // decodes to a space
    [InlineData("/nul%00byte")]            // decodes to NUL
    [InlineData("/tab%09stop")]            // decodes to a control character (HT)
    [InlineData("/line%0Afeed")]           // decodes to a control character (LF)
    [InlineData("/carriage%0Dreturn")]     // decodes to a control character (CR)
    [InlineData("/hash%23fragment")]       // decodes to '#'
    [InlineData("/query%3Fmark")]          // decodes to '?'
    [InlineData("/literal space")]         // an illegal character on the wire itself
    [InlineData("relative/path")]          // not origin-form: no leading '/'
    public async Task Dispatch_OnMalformedPath_ShouldResetStreamWithProtocolErrorAndKeepServing(string rawPath)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act — stream 1 carries the malformed :path; stream 3 is an ordinary sibling request.
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get(rawPath));
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/ok"));

        IHttpContext sibling = await peer.ReceiveContextAsync();
        sibling.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("ok"));
        await peer.ConnectionContext.SendAsync(sibling).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsData && frame.StreamId == 3 && frame.EndStream),
            "the response on stream 3");
        await peer.SyncAsync();

        // Assert — the sibling was the first request dispatched, and it was answered normally.
        sibling.Request.Path.Value.ShouldBe("/ok");
        Encoding.ASCII.GetString(peer.Output.DataPayload(3)).ShouldBe("ok");

        // Stream 1 got exactly one frame — RST_STREAM(PROTOCOL_ERROR) — and the connection was never
        // torn down.
        IReadOnlyList<Http2WireFrame> malformed = peer.Output.ForStream(1);
        Http2WireFrame reset = malformed.ShouldHaveSingleItem();
        reset.IsRstStream.ShouldBeTrue();
        reset.GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Malformed :path: A legal encoded :path should still decode as on HTTP/1.1")]
    public async Task Dispatch_OnLegalEncodedPath_ShouldDecodePath()
    {
        // The per-stream rejection only changes how a failed decode is scoped — a legal target still
        // decodes exactly as on the other transports (%2e%2e to "..", %2F kept encoded).
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/static/%2e%2e/a%2Fb"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        exchange.Request.Path.Value.ShouldBe("/static/../a%2Fb");
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsRstStream);
    }
}
