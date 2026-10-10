using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// HTTP/2 and HTTP/3 response heads never carry a connection-specific field (#1328, RFC 9113 §8.2.2,
/// RFC 9114 §4.2): a client treats a message carrying <c>Connection</c>, <c>Keep-Alive</c>,
/// <c>Proxy-Connection</c>, <c>Transfer-Encoding</c>, <c>Upgrade</c>, or a <c>TE</c> other than
/// <c>trailers</c> as malformed and may reset the stream. An application, middleware ported from
/// HTTP/1.1, or a proxy can set them, so the transports drop them from buffered and streamed final
/// responses and from early hints, and keep every other field.
/// </summary>
public class HttpResponseConnectionFieldTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly HttpHeaderKey _ordinaryField = new("x-trace");

    // Each connection-specific field with a value an HTTP/1.1 component would plausibly set.
    private static readonly (string Name, string Value)[] _connectionSpecificFields =
    [
        ("Connection", "keep-alive"),
        ("Keep-Alive", "timeout=5, max=100"),
        ("Proxy-Connection", "keep-alive"),
        ("Transfer-Encoding", "chunked"),
        ("Upgrade", "websocket"),
        ("TE", "gzip"),
    ];

    /// <summary>The response head a test sends.</summary>
    public enum ResponseHead
    {
        /// <summary>A buffered final response.</summary>
        Buffered,

        /// <summary>A streamed final response.</summary>
        Streamed,

        /// <summary>A <c>103 Early Hints</c> interim response.</summary>
        EarlyHints,
    }

    /// <summary>Every response head paired with every connection-specific field.</summary>
    public static TheoryData<ResponseHead, string, string> ConnectionSpecificFields
    {
        get
        {
            TheoryData<ResponseHead, string, string> data = new();

            foreach (ResponseHead head in Enum.GetValues<ResponseHead>())
            {
                foreach ((string name, string value) in _connectionSpecificFields)
                {
                    data.Add(head, name, value);
                }
            }

            return data;
        }
    }

    // -------------------------------------------------------------------- HTTP/2

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Head: A connection-specific field should be dropped")]
    [MemberData(nameof(ConnectionSpecificFields))]
    public async Task Http2ResponseHead_WithConnectionSpecificField_ShouldDropIt(ResponseHead head, string name, string value)
    {
        // Arrange, Act
        Dictionary<string, string> sent = await SendHttp2HeadAsync(head, name, value);

        // Assert — the field is gone; the head and its other fields are not.
        sent.ShouldNotContainKey(name);
        sent[":status"].ShouldBe(ExpectedStatus(head));
        sent[_ordinaryField.Value].ShouldBe("kept");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Head: TE: trailers should be kept")]
    [InlineData(ResponseHead.Buffered)]
    [InlineData(ResponseHead.Streamed)]
    [InlineData(ResponseHead.EarlyHints)]
    public async Task Http2ResponseHead_WithTeTrailers_ShouldKeepIt(ResponseHead head)
    {
        // Arrange, Act
        Dictionary<string, string> sent = await SendHttp2HeadAsync(head, "TE", "trailers");

        // Assert
        sent["te"].ShouldBe("trailers");
        sent[_ordinaryField.Value].ShouldBe("kept");
    }

    // -------------------------------------------------------------------- HTTP/3

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Response Head: A connection-specific field should be dropped")]
    [MemberData(nameof(ConnectionSpecificFields))]
    public async Task Http3ResponseHead_WithConnectionSpecificField_ShouldDropIt(ResponseHead head, string name, string value)
    {
        // Arrange, Act
        Dictionary<string, string> sent = await SendHttp3HeadAsync(head, name, value);

        // Assert
        sent.ShouldNotContainKey(name);
        sent[":status"].ShouldBe(ExpectedStatus(head));
        sent[_ordinaryField.Value].ShouldBe("kept");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Response Head: TE: trailers should be kept")]
    [InlineData(ResponseHead.Buffered)]
    [InlineData(ResponseHead.Streamed)]
    [InlineData(ResponseHead.EarlyHints)]
    public async Task Http3ResponseHead_WithTeTrailers_ShouldKeepIt(ResponseHead head)
    {
        // Arrange, Act
        Dictionary<string, string> sent = await SendHttp3HeadAsync(head, "TE", "trailers");

        // Assert
        sent["te"].ShouldBe("trailers");
        sent[_ordinaryField.Value].ShouldBe("kept");
    }

    // ------------------------------------------------------------------- Helpers

    private static string ExpectedStatus(ResponseHead head) => head == ResponseHead.EarlyHints ? "103" : "200";

    private static void Configure(HttpConnectionListenerOptions options)
    {
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        options.Interceptors.Add(HttpInterimResponses.CreateInterceptor());
    }

    // Opens stream 1 with a GET, sends the head under test, and returns the first field section the
    // server wrote on the stream: the head under test.
    private static async Task<Dictionary<string, string>> SendHttp2HeadAsync(ResponseHead head, string name, string value)
    {
        HttpConnectionListenerOptions options = new();
        Configure(options);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/page"));
        IHttpContext context = await peer.ReceiveContextAsync();

        await SendHeadAsync(context, peer.ConnectionContext, head, name, value);

        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(
            observed => observed.Any(frame => frame.StreamId == 1 && frame.EndStream),
            "the end of the response on stream 1");
        Http2WireFrame first = frames.First(frame => frame.IsHeaders && frame.StreamId == 1);

        return HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(first.Payload);
    }

    // Opens a request stream with a GET, sends the head under test, and returns the first field section
    // the server wrote on the stream: the head under test.
    private static async Task<Dictionary<string, string>> SendHttp3HeadAsync(ResponseHead head, string name, string value)
    {
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: Configure);
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/page", "https", "a"));
        await request.Output.CompleteAsync();
        IHttpContext context = await peer.NextContextAsync();

        await SendHeadAsync(context, peer.ConnectionContext, head, name, value);

        Http3FrameCollector output = new(request);
        await output.ReadUntilAsync(collector => collector.IsCompleted, "the end of the response");
        (long _, byte[] payload) = output.Frames.First(frame => frame.FrameType == 0x1);

        return HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(payload);
    }

    // Sends the head under test carrying the connection-specific field beside an ordinary one, then
    // finishes the exchange so the connection can close without waiting for it.
    private static async Task SendHeadAsync(IHttpContext context, IHttpConnectionContext connection, ResponseHead head, string name, string value)
    {
        HttpHeaderKey key = new(name);
        context.Response.StatusCode = HttpStatusCode.Ok;

        switch (head)
        {
            case ResponseHead.Buffered:
                context.Response.Headers[key] = value;
                context.Response.Headers[_ordinaryField] = "kept";
                context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("body"));
                break;

            case ResponseHead.Streamed:
                context.Response.Headers[key] = value;
                context.Response.Headers[_ordinaryField] = "kept";
                await context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("body"));
                await context.Response.Streaming.FlushAsync();
                break;

            case ResponseHead.EarlyHints:
                HttpHeaderCollection hints = new();
                hints[HttpHeaderKey.Link] = "</style.css>; rel=preload; as=style";
                hints[key] = value;
                hints[_ordinaryField] = "kept";
                await context.Features.Get<IHttpInterimResponseFeature>()!
                    .SendInterimResponseAsync(HttpStatusCode.EarlyHints, hints)
                    .AsTask()
                    .WaitAsync(_timeout);
                break;
        }

        await connection.SendAsync(context).AsTask().WaitAsync(_timeout);
    }
}
