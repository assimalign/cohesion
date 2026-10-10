using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
/// RFC 8441 §4), and a request that breaks the extended CONNECT rules — an empty <c>:protocol</c>,
/// a <c>:protocol</c> on another method, an extended CONNECT missing a pseudo-header — is malformed
/// in the same way. So is a classic CONNECT without a host and port in <c>:authority</c> or with a
/// <c>:scheme</c> or <c>:path</c>, and an asterisk-form <c>:path</c> outside OPTIONS (#1334), and so is a
/// <c>:method</c> that is not a token or is longer than the 32 characters <see cref="HttpMethod"/> holds.
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

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: A request with an empty :protocol should reset only its stream")]
    [InlineData("GET")]
    [InlineData("CONNECT")]
    public async Task ReceiveAsync_OnEmptyProtocol_ShouldResetStreamWithProtocolError(string method)
    {
        // Arrange — RFC 9110 §5.6.2: a protocol name is a token (1*tchar), so an empty :protocol names
        // no protocol and the request is malformed on any method (RFC 8441 §4, #1369).
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        (string Name, string Value)[] fields = Http2TestPeer.Request(method, "/chat", (":protocol", string.Empty));

        // Act
        IHttpContext next = await SendMalformedThenValidAsync(peer, fields);

        // Assert
        await AssertResetBeforeDispatchAsync(peer, next);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: A :protocol on a request other than CONNECT should reset only its stream")]
    public async Task ReceiveAsync_OnProtocolWithoutConnect_ShouldResetStreamWithProtocolError()
    {
        // Arrange — RFC 8441 §4: :protocol is only valid on a CONNECT.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        (string Name, string Value)[] fields = Http2TestPeer.Request("GET", "/malformed", (":protocol", "websocket"));

        // Act
        IHttpContext next = await SendMalformedThenValidAsync(peer, fields);

        // Assert
        await AssertResetBeforeDispatchAsync(peer, next);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: An extended CONNECT missing a required pseudo-header should reset only its stream")]
    [InlineData(":scheme")]
    [InlineData(":path")]
    [InlineData(":authority")]
    public async Task ReceiveAsync_OnExtendedConnectMissingPseudoHeader_ShouldResetStreamWithProtocolError(string missing)
    {
        // Arrange — RFC 8441 §4: unlike a classic CONNECT, an extended CONNECT MUST carry :scheme, :path
        // and :authority.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        (string Name, string Value)[] fields = Http2TestPeer.Request("CONNECT", "/chat", (":protocol", "websocket"))
            .Where(field => field.Name != missing)
            .ToArray();

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

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: A classic CONNECT without a host and port, or with :scheme or :path, should reset only its stream")]
    [InlineData(null, null, null)]
    [InlineData(null, null, "")]
    [InlineData(null, null, "upstream.test")]
    [InlineData("https", null, "upstream.test:443")]
    [InlineData(null, "/", "upstream.test:443")]
    [InlineData("https", "/tunnel", "upstream.test:443")]
    public async Task ReceiveAsync_OnMalformedClassicConnect_ShouldResetStreamWithProtocolError(string? scheme, string? path, string? authority)
    {
        // Arrange — RFC 9113 §8.5: a CONNECT without :protocol carries the host and port to connect to in
        // :authority, and its :scheme and :path MUST be omitted (#1334).
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        List<(string Name, string Value)> fields = [(":method", "CONNECT")];

        if (scheme is not null)
        {
            fields.Add((":scheme", scheme));
        }

        if (path is not null)
        {
            fields.Add((":path", path));
        }

        if (authority is not null)
        {
            fields.Add((":authority", authority));
        }

        // Act
        IHttpContext next = await SendMalformedThenValidAsync(peer, fields.ToArray());

        // Assert
        await AssertResetBeforeDispatchAsync(peer, next);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: An asterisk-form :path outside OPTIONS, or one that is not exactly '*', should reset only its stream")]
    [InlineData("GET", "*")]
    [InlineData("POST", "*")]
    [InlineData("GET", "*foo")]
    [InlineData("OPTIONS", "*foo")]
    [InlineData("OPTIONS", "*?x=1")]
    public async Task ReceiveAsync_OnAsteriskFormOutsideOptions_ShouldResetStreamWithProtocolError(string method, string path)
    {
        // Arrange — RFC 9110 §7.1 and RFC 9113 §8.3.1: asterisk-form is the single character '*', and only
        // OPTIONS may use it, as HTTP/1.1 already enforces (#1334).
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act
        IHttpContext next = await SendMalformedThenValidAsync(peer, Http2TestPeer.Request(method, path));

        // Assert
        await AssertResetBeforeDispatchAsync(peer, next);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: A :method that is not a token, or is longer than 32 characters, should reset only its stream")]
    [InlineData("G T")]
    [InlineData("GETé")]
    [InlineData("G@T")]
    [InlineData("")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFG")]
    public async Task ReceiveAsync_OnMethodThatHttpMethodCannotCarry_ShouldResetStreamWithProtocolError(string method)
    {
        // Arrange — RFC 9110 §9.1: a method is a token, and HttpMethod holds at most 32 characters. Such a
        // :method once threw ArgumentException out of HttpMethod.GetCanonicalizedValue, which the frame
        // pump's last-resort handler answered by dropping the whole connection without a GOAWAY. The value
        // is written as Latin-1, as HPACK carries it (#1387).
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        byte[] block = HPackTestEncoder.Block(
            Latin1Literal(":method", method),
            HPackTestEncoder.Literal(":scheme", "https"),
            HPackTestEncoder.Literal(":path", "/malformed"),
            HPackTestEncoder.Literal(":authority", "api.test"));

        // Act
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(1, endStream: true, block));
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();

        // Assert
        await AssertResetBeforeDispatchAsync(peer, next);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: An extension method of 32 characters should be dispatched")]
    public async Task ReceiveAsync_OnExtensionMethodOf32Characters_ShouldDispatchRequest()
    {
        // Arrange — the longest method HttpMethod carries.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        string method = new('M', 32);

        // Act
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request(method, "/extension"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Assert
        exchange.Request.Method.Value.ShouldBe(method);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsRstStream || frame.IsGoAway);

        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Pseudo-Headers: A valid extended CONNECT should still be dispatched")]
    public async Task ReceiveAsync_OnExtendedConnect_ShouldDispatchRequest()
    {
        // Arrange — RFC 8441 §4: an extended CONNECT carries :scheme and :path, which the classic-CONNECT rule
        // forbids only when :protocol is absent.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("CONNECT", "/chat", (":protocol", "websocket")));
        IHttpContext tunnel = await peer.ReceiveContextAsync();

        // Assert
        tunnel.Request.Method.ShouldBe(HttpMethod.Connect);
        tunnel.Request.Path.Value.ShouldBe("/chat");
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

    /// <summary>
    /// An HPACK Literal Header Field without Indexing and with a literal name (RFC 7541 §6.2.2), each
    /// string's octets its Latin-1 encoding, so a character in <c>0x80</c>–<c>0xFF</c> reaches the wire as
    /// the octet of the same value.
    /// </summary>
    private static byte[] Latin1Literal(string name, string value)
    {
        byte[] nameOctets = Encoding.Latin1.GetBytes(name);
        byte[] valueOctets = Encoding.Latin1.GetBytes(value);

        // One-octet length prefixes: every string here is shorter than 127 octets.
        nameOctets.Length.ShouldBeLessThan(127);
        valueOctets.Length.ShouldBeLessThan(127);

        return [0x00, (byte)nameOctets.Length, .. nameOctets, (byte)valueOctets.Length, .. valueOctets];
    }
}
