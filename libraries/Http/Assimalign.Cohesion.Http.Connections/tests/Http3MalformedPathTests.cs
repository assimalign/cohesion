using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies that a malformed <c>:path</c> on HTTP/3 is a per-stream error (issue #937, RFC 9114
/// §4.1.2): the offending request stream is reset with <c>H3_MESSAGE_ERROR</c>, and the connection
/// keeps accepting and serving its other streams. The percent-decode itself is the shared
/// <c>HttpPath.FromUriComponent</c>, unchanged, so HTTP/1.1 and HTTP/2 reject the same targets.
/// </summary>
public class Http3MalformedPathTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Malformed :path: A :path that is not a legal path should reset only its stream with H3_MESSAGE_ERROR")]
    [InlineData("/space%20name")]          // decodes to a space
    [InlineData("/nul%00byte")]            // decodes to NUL
    [InlineData("/tab%09stop")]            // decodes to a control character (HT)
    [InlineData("/line%0Afeed")]           // decodes to a control character (LF)
    [InlineData("/carriage%0Dreturn")]     // decodes to a control character (CR)
    [InlineData("/hash%23fragment")]       // decodes to '#'
    [InlineData("/query%3Fmark")]          // decodes to '?'
    [InlineData("/literal space")]         // an illegal character on the wire itself
    [InlineData("relative/path")]          // not origin-form: no leading '/'
    public async Task Receive_OnMalformedPath_ShouldResetStreamWithMessageErrorAndKeepServing(string rawPath)
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();

        // Act — the first request stream carries the malformed :path; the second is an ordinary sibling.
        Connection malformed = await peer.OpenRequestStreamAsync();
        await malformed.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", rawPath, "https", "a"));
        malformed.Output.Complete();

        Connection sibling = await peer.OpenRequestStreamAsync();
        await sibling.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/ok", "https", "a"));
        sibling.Output.Complete();

        IHttpContext exchange = await peer.NextContextAsync();
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("ok"));
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);

        // Assert — the malformed stream never became an exchange: the sibling was the first one
        // dispatched, and it was answered normally.
        exchange.Request.Path.Value.ShouldBe("/ok");
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(sibling));
        Encoding.ASCII.GetString(frames.Single(frame => frame.FrameType == (long)Http3FrameType.Data).Payload).ShouldBe("ok");

        // The malformed stream alone was reset, with H3_MESSAGE_ERROR (RFC 9114 §4.1.2) …
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(malformed));
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.MessageError);

        // … and the connection was not: it is still open and dispatches a new request stream.
        peer.Server.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        Connection later = await peer.OpenRequestStreamAsync();
        await later.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/later", "https", "a"));
        later.Output.Complete();
        (await peer.NextContextAsync()).Request.Path.Value.ShouldBe("/later");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Malformed :path: A legal encoded :path should still decode as on HTTP/1.1 and HTTP/2")]
    public async Task Receive_OnLegalEncodedPath_ShouldDecodePath()
    {
        // The per-stream rejection only changes how a failed decode is scoped — a legal target still
        // decodes exactly as on the other transports (%2e%2e to "..", %2F kept encoded).
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/static/%2e%2e/a%2Fb", "https", "a"));
        request.Output.Complete();

        IHttpContext exchange = await peer.NextContextAsync();

        exchange.Request.Path.Value.ShouldBe("/static/../a%2Fb");
    }
}
