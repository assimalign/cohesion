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
/// obs-text on every version (#1387). A field value octet in <c>0x80</c>–<c>0xFF</c> is valid (RFC 9110
/// §5.5), and HTTP/1.1 (#1341), HPACK, and QPACK all decode it as the Latin-1 character of the same value,
/// raw or Huffman-coded (RFC 7541 §5.2, RFC 9204 §4.1.2), so an application reads the same string whichever
/// version carried it. A field name stays a token, and the request target and host stay visible US-ASCII,
/// as HTTP/1.1 holds its request line and <c>Host</c>: an obs-text octet in <c>:path</c>, <c>:authority</c>,
/// or <c>Host</c> makes an HTTP/2 or HTTP/3 request malformed, and only its stream is reset. A
/// <c>Content-Length</c> loses SP and HTAB around its elements and nothing else, so a no-break space or a
/// next-line octet beside its digits makes an HTTP/3 request malformed, as it gets HTTP/1.1's 400.
/// </summary>
public class HttpObsTextFieldValueTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - obs-text: A field value octet above 0x7F should read as the same Latin-1 character on HTTP/1.1, HTTP/2 and HTTP/3")]
    [InlineData(0x80, false)]
    [InlineData(0x85, false)]
    [InlineData(0xA0, false)]
    [InlineData(0xE9, false)]
    [InlineData(0xFF, false)]
    [InlineData(0xA0, true)]
    [InlineData(0xE9, true)]
    [InlineData(0xFF, true)]
    public async Task ReceiveAsync_OnObsTextFieldValue_ShouldReadTheSameOnEveryVersion(int octet, bool huffman)
    {
        // Arrange — the octet sits inside the value, where no version trims it.
        string value = $"a{(char)octet}b";

        // Act
        string http1 = await ReceiveHttp1FieldAsync(value);
        string http2 = await ReceiveHttp2FieldAsync(value, huffman);
        string http3 = await ReceiveHttp3FieldAsync(value, huffman);

        // Assert
        http1.ShouldBe(value);
        http2.ShouldBe(value);
        http3.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - obs-text: An HTTP/2 :path, :authority or Host with an octet above 0x7F should reset only its stream")]
    [InlineData(":path", "/adminÿx")]
    [InlineData(":path", "/café")]
    [InlineData(":authority", "api.test ")]
    [InlineData("host", "api.tést")]
    public async Task Http2ReceiveAsync_OnObsTextInRequestTarget_ShouldResetStreamWithProtocolError(string name, string value)
    {
        // Arrange — HTTP/1.1 answers the same octets in its request line or Host with 400 (#1341). Decoded as
        // ASCII, "/admin\xFFx" became the path "/admin" and the query "x".
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        byte[] block = HPackTestEncoder.Block(
            HPackTestEncoder.Literal(":method", "GET"),
            HPackTestEncoder.Literal(":scheme", "http"),
            name == ":path" ? HPackLiteral(":path", value) : HPackTestEncoder.Literal(":path", "/admin"),
            name == ":authority" ? HPackLiteral(":authority", value) : HPackTestEncoder.Literal(":authority", "api.test"),
            name == "host" ? HPackLiteral("host", value) : HPackTestEncoder.Literal("x-sibling", "none"));

        // Act
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(1, endStream: true, block));
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();

        // Assert — the malformed stream never reached the application; the connection serves the next one.
        next.Request.Path.Value.ShouldBe("/next");
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);

        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - obs-text: An HTTP/3 :path, :authority or Host with an octet above 0x7F should reset only its stream")]
    [InlineData(":path", "/adminÿx")]
    [InlineData(":path", "/café")]
    [InlineData(":authority", "api.test ")]
    [InlineData("host", "api.tést")]
    public async Task Http3ReceiveAsync_OnObsTextInRequestTarget_ShouldResetStreamWithMessageError(string name, string value)
    {
        // Arrange
        byte[] malformedPayload = Http3Request(
            (":method", "GET", false),
            (":scheme", "https", false),
            (":path", name == ":path" ? value : "/admin", false),
            (":authority", name == ":authority" ? value : "api.test", false),
            (name == "host" ? "host" : "x-sibling", name == "host" ? value : "none", false));

        // Act and Assert
        await AssertHttp3ResetWhileSiblingIsServedAsync(malformedPayload);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - obs-text: An HTTP/3 Content-Length with a no-break space or next-line octet around its digits should reset only its stream")]
    [InlineData("5 ")]
    [InlineData("\u00855")]
    [InlineData("5, 5")]
    [InlineData("5\u0085,5")]
    public async Task Http3ReceiveAsync_OnObsTextWhitespaceInContentLength_ShouldResetStreamWithMessageError(string contentLength)
    {
        // Arrange — RFC 9110 §5.6.3: OWS is SP and HTAB only. A Unicode trim (StringSplitOptions.TrimEntries)
        // also strips U+00A0 and U+0085, which QPACK decodes from obs-text, so "5\xA0" was enforced as 5 while
        // HTTP/1.1 answers it with 400 (#1341) and an intermediary comparing the value exactly sees no length.
        byte[] malformedPayload = Http3Request(
            (":method", "POST", false),
            (":scheme", "https", false),
            (":path", "/upload", false),
            (":authority", "api.test", false),
            ("content-length", contentLength, false));

        // Act and Assert
        await AssertHttp3ResetWhileSiblingIsServedAsync(malformedPayload);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - obs-text: An HTTP/3 Content-Length list with SP around its elements should still be read")]
    public async Task Http3ReceiveAsync_OnContentLengthListWithOptionalWhitespace_ShouldReadTheLength()
    {
        // Arrange — "5, 5" is two identical values separated by OWS (RFC 9110 §8.6, §5.6.1).
        byte[] payload = Combine(
            Http3Request(
                (":method", "POST", false),
                (":scheme", "https", false),
                (":path", "/upload", false),
                (":authority", "api.test", false),
                ("content-length", "5, 5", false)),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello")));
        TestMultiplexedConnection connection = new(new TestConnection(payload));
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        // Act
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        using StreamReader reader = new(enumerator.Current.Request.Body);

        // Assert
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - obs-text: An HTTP/2 field name with an octet above 0x7F should still reset only its stream")]
    public async Task Http2ReceiveAsync_OnObsTextInFieldName_ShouldResetStreamWithProtocolError()
    {
        // Arrange — a name is a token (RFC 9110 §5.1), and no octet above 0x7F is a tchar (#1376).
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        byte[] block = HPackTestEncoder.Block(HPackTestEncoder.RequestHead("GET", "/admin"), HPackLiteral("x-café", "1"));

        // Act
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(1, endStream: true, block));
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();

        // Assert
        next.Request.Path.Value.ShouldBe("/next");
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);

        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - obs-text: An HTTP/3 field name with an octet above 0x7F should still reset only its stream")]
    public async Task Http3ReceiveAsync_OnObsTextInFieldName_ShouldResetStreamWithMessageError()
    {
        // Arrange
        byte[] malformedPayload = Http3Request(
            (":method", "GET", false),
            (":scheme", "https", false),
            (":path", "/admin", false),
            (":authority", "api.test", false),
            ("x-café", "1", false));

        // Act and Assert
        await AssertHttp3ResetWhileSiblingIsServedAsync(malformedPayload);
    }

    // ------------------------------------------------------------ helpers

    private static async Task<string> ReceiveHttp1FieldAsync(string value)
    {
        byte[] payload = Encoding.Latin1.GetBytes($"GET /obs HTTP/1.1\r\nHost: api.test\r\nX-Obs: {value}\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();

        return enumerator.Current.Request.Headers[new HttpHeaderKey("x-obs")].Value;
    }

    private static async Task<string> ReceiveHttp2FieldAsync(string value, bool huffman)
    {
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        byte[] block = HPackTestEncoder.Block(HPackTestEncoder.RequestHead("GET", "/obs"), HPackLiteral("x-obs", value, huffman));
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(1, endStream: true, block));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        string received = exchange.Request.Headers[new HttpHeaderKey("x-obs")].Value;
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        return received;
    }

    private static async Task<string> ReceiveHttp3FieldAsync(string value, bool huffman)
    {
        byte[] payload = Http3Request(
            (":method", "GET", false),
            (":scheme", "https", false),
            (":path", "/obs", false),
            (":authority", "api.test", false),
            ("x-obs", value, huffman));
        TestMultiplexedConnection connection = new(new TestConnection(payload));
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();

        return enumerator.Current.Request.Headers[new HttpHeaderKey("x-obs")].Value;
    }

    private static async Task AssertHttp3ResetWhileSiblingIsServedAsync(byte[] malformedPayload)
    {
        TestConnection malformed = new(malformedPayload);
        TestConnection sibling = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/ok", "https", "api.test"));
        TestMultiplexedConnection connection = new(malformed, sibling);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        // The malformed stream never became an exchange; the sibling is the first dispatched.
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/ok");
        malformed.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.MessageError);
        connection.State.ShouldBe(ConnectionState.Open);
    }

    private static byte[] Combine(params byte[][] parts)
    {
        using MemoryStream buffer = new();

        foreach (byte[] part in parts)
        {
            buffer.Write(part, 0, part.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// An HPACK Literal Header Field without Indexing and with a literal name (RFC 7541 §6.2.2), its
    /// octets the Latin-1 encoding of each string, the value optionally Huffman-coded (RFC 7541 §5.2).
    /// </summary>
    private static byte[] HPackLiteral(string name, string value, bool huffman = false)
    {
        using MemoryStream buffer = new();
        buffer.WriteByte(0x00);
        WriteHPackString(buffer, Encoding.Latin1.GetBytes(name), huffman: false);
        WriteHPackString(buffer, huffman ? HttpProtocolPayloadFactory.HuffmanEncode(value) : Encoding.Latin1.GetBytes(value), huffman);
        return buffer.ToArray();
    }

    private static void WriteHPackString(Stream stream, byte[] octets, bool huffman)
    {
        // A one-octet length prefix: every string here is shorter than 127 octets.
        octets.Length.ShouldBeLessThan(127);
        stream.WriteByte((byte)((huffman ? 0x80 : 0x00) | octets.Length));
        stream.Write(octets, 0, octets.Length);
    }

    /// <summary>
    /// An HTTP/3 request stream's HEADERS frame whose field section holds the fields as QPACK Literal Field
    /// Lines with Literal Names (RFC 9204 §4.5.6), each string's octets its Latin-1 encoding, a value
    /// optionally Huffman-coded.
    /// </summary>
    private static byte[] Http3Request(params (string Name, string Value, bool Huffman)[] fields)
    {
        using MemoryStream section = new();
        section.WriteByte(0x00); // Required Insert Count = 0
        section.WriteByte(0x00); // S = 0, Delta Base = 0

        foreach ((string name, string value, bool huffman) in fields)
        {
            byte[] nameOctets = Encoding.Latin1.GetBytes(name);
            QPackPrefixedInteger.Encode(section, nameOctets.Length, 3, 0b0010_0000);
            section.Write(nameOctets, 0, nameOctets.Length);

            byte[] valueOctets = huffman ? HttpProtocolPayloadFactory.HuffmanEncode(value) : Encoding.Latin1.GetBytes(value);
            QPackPrefixedInteger.Encode(section, valueOctets.Length, 7, huffman ? (byte)0x80 : (byte)0x00);
            section.Write(valueOctets, 0, valueOctets.Length);
        }

        return HttpProtocolPayloadFactory.CreateHttp3Frame(0x1, section.ToArray());
    }
}
