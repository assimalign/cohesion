using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The HTTP/3 decoded field-section limit (#1082). The server advertises
/// <c>SETTINGS_MAX_FIELD_SECTION_SIZE</c> and the QPACK decoder enforces it on request heads and trailer
/// sections, counting each field as RFC 9114 §4.2.2 does: name + value + 32 octets. A head over it is
/// answered 431. A trailer section over it is answered 431 while the response has not started, and
/// resets the stream with <c>H3_MESSAGE_ERROR</c> after. Repeated fields combine in linear time, so a
/// raised limit cannot bring back the quadratic cost.
/// </summary>
public class Http3FieldSectionLimitTests
{
    // QPACK static table entry 29 is "accept: */*" (RFC 9204 Appendix A): one octet as an indexed field
    // line, 6 + 3 + 32 = 41 octets decoded.
    private const byte acceptAllFieldLine = 0xC0 | 29;
    private const int acceptAllDecodedSize = 41;

    // Combining a field repeated this often by copying the combined value per repeat allocates hundreds
    // of megabytes; combining it in linear time allocates well under one.
    private const int manyRepeats = 10_000;
    private const long linearAllocationBound = 16L * 1024 * 1024;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------ the QPACK decoder

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: A section exactly at the limit should decode")]
    public void Decode_OnSectionAtLimit_ShouldDecode()
    {
        // Arrange
        byte[] section = CreateStaticFieldSection(fieldCount: 10);

        // Act
        List<(string Name, string Value)> fields = QPackFieldSectionDecoder.Decode(section, 10 * acceptAllDecodedSize);

        // Assert
        fields.Count.ShouldBe(10);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: A section one octet over the limit should be refused with 431")]
    public void Decode_OnSectionOneOctetOverLimit_ShouldThrow431()
    {
        // Arrange
        byte[] section = CreateStaticFieldSection(fieldCount: 10);

        // Act
        Http3LimitExceededException exception = Should.Throw<Http3LimitExceededException>(
            () => QPackFieldSectionDecoder.Decode(section, (10 * acceptAllDecodedSize) - 1));

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.RequestHeaderFieldsTooLarge);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: The decoder should stop at the field that crosses the limit")]
    public void Decode_OnSectionOverLimit_ShouldStopBeforeDecodingTheRest()
    {
        // Arrange — the third field crosses the limit, and after it comes an index past the static table,
        // which fails the decode as malformed if the decoder reads that far.
        byte[] section = [.. CreateStaticFieldSection(fieldCount: 3), 0xFF, 36];

        // Act + Assert
        Should.Throw<Http3LimitExceededException>(() => QPackFieldSectionDecoder.Decode(section, 2 * acceptAllDecodedSize));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: References to a large dynamic-table entry should stop at the limit")]
    public async Task DecodeRequestAsync_OnDynamicReferencesOverLimit_ShouldThrow431()
    {
        // Arrange — one 1,000-octet entry referenced by twenty one-octet field lines: a 22-octet section that
        // decodes to 20 * (5 + 1000 + 32) = 20,740 octets, over the 16 KB default.
        QPackDecoderState state = new(new Http3QPackOptions { MaxTableCapacity = 4096, MaxBlockedStreams = 8 });
        state.ApplyEncoderInstructions(
            [
                .. HttpProtocolPayloadFactory.QPackSetCapacity(4096),
                .. HttpProtocolPayloadFactory.QPackInsertWithLiteralName("x-big", new string('v', 1000)),
            ],
            out int insertions);
        insertions.ShouldBe(1);

        // Prefix: encoded Required Insert Count 2 (RIC 1), Delta Base 0 (Base 1). Each 0x80 is a dynamic
        // indexed field line with relative index 0, absolute index 0.
        byte[] section = [0x02, 0x00, .. Enumerable.Repeat((byte)0x80, 20)];

        // Act
        Http3LimitExceededException exception = await Should.ThrowAsync<Http3LimitExceededException>(
            () => state.DecodeRequestAsync(section, CancellationToken.None));

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.RequestHeaderFieldsTooLarge);
    }

    // ------------------------------------------------------------ SETTINGS

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: SETTINGS should advertise SETTINGS_MAX_FIELD_SECTION_SIZE")]
    [InlineData(null, Http3QPackOptions.DefaultMaxFieldSectionSize)]
    [InlineData(4096L, 4096L)]
    public async Task ReceiveAsync_OnConnectionOpen_ShouldAdvertiseMaxFieldSectionSize(long? configured, long advertised)
    {
        // Arrange
        TestMultiplexedConnection connection = new(new TestConnection(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/s", "https", "a")));
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), http3 =>
        {
            if (configured is { } value)
            {
                http3.QPack.MaxFieldSectionSize = value;
            }
        });

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act — driving the receive loop opens the control stream and writes SETTINGS first.
        await using (IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator())
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
        }

        (long _, IReadOnlyList<(long FrameType, byte[] Payload)> frames) =
            HttpProtocolPayloadFactory.ParseHttp3UnidirectionalStream(await connection.ControlStream!.ReadOutputAsync());

        // Assert — RFC 9114 §7.2.4.1: SETTINGS_MAX_FIELD_SECTION_SIZE is identifier 0x06.
        IReadOnlyDictionary<long, long> settings = HttpProtocolPayloadFactory.DecodeHttp3Settings(frames[0].Payload);
        settings[0x06].ShouldBe(advertised);
    }

    // ------------------------------------------------------------ request heads

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: A head over the limit should be answered 431 while other streams are served")]
    public async Task ReceiveAsync_OnHeadOverLimit_ShouldAnswer431AndServeOtherStreams()
    {
        // Arrange — 1,000 one-octet static references: a HEADERS frame of about 1 KB, far under the 32 KB
        // frame cap, that decodes to about 41 KB, over the 16 KB default.
        TestConnection oversized = new(CreateRequestWithStaticFields("/big", fieldCount: 1000));
        TestConnection sibling = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/ok", "https", "a"));
        TestMultiplexedConnection connection = new(oversized, sibling);

        // Act
        List<string> dispatched = await ReceiveAllPathsAsync(connection);

        // Assert — the oversized request never reached the application: the transport answered it.
        dispatched.ShouldBe(["/ok"]);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await oversized.ReadOutputAsync().WaitAsync(_timeout));
        frames.Count.ShouldBe(1);
        frames[0].FrameType.ShouldBe((long)Http3FrameType.Headers);

        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload);
        headers[":status"].ShouldBe("431");
        headers["content-length"].ShouldBe("0");

        oversized.IsAborted.ShouldBeFalse();
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: A raised limit should admit a head the default refuses")]
    public async Task ReceiveAsync_OnRaisedLimit_ShouldDispatchLargeHead()
    {
        // Arrange — the same 41 KB head, under a 64 KB limit.
        TestMultiplexedConnection connection = new(new TestConnection(CreateRequestWithStaticFields("/big", fieldCount: 1000)));

        // Act
        IHttpContext context = await ReceiveSingleAsync(connection, static http3 => http3.QPack.MaxFieldSectionSize = 64 * 1024);

        // Assert
        context.Request.Path.Value.ShouldBe("/big");
        context.Request.Headers[HttpHeaderKey.Accept].Count.ShouldBe(1000);
    }

    // ------------------------------------------------------------ trailer sections

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: A trailer section over the limit should fail the body read and be answered 431")]
    public async Task ReadBody_OnTrailersOverLimit_ShouldFailReadAndAnswer431()
    {
        // Arrange — under a 256-octet limit the head's four pseudo-header fields (174 octets) fit, and the
        // trailer section's ten fields (10 * (3 + 20 + 32) = 550 octets) do not.
        TestConnection stream = new(CreateRequestWithOversizedTrailers());
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), static http3 => http3.QPack.MaxFieldSectionSize = 256);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        // Act
        using (StreamReader reader = new(context.Request.Body, leaveOpen: true))
        {
            await Should.ThrowAsync<IOException>(() => reader.ReadToEndAsync());
        }

        await connectionContext.SendAsync(context);

        // Assert — the response had not started, so the exchange is answered 431 with no content.
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync().WaitAsync(_timeout));
        frames.Count.ShouldBe(1);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"].ShouldBe("431");

        context.Request.Trailers.Count.ShouldBe(0);
        stream.IsAborted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: A trailer section over the limit after the response started should reset with H3_MESSAGE_ERROR")]
    public async Task ReadBody_OnTrailersOverLimitAfterResponseStarted_ShouldResetWithMessageError()
    {
        // Arrange — the response head is on the wire before the body is read, so no 431 can follow it.
        TestConnection stream = new(CreateRequestWithOversizedTrailers());
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), static http3 => http3.QPack.MaxFieldSectionSize = 256);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        IHttpResponseStreamingFeature streaming = context.Response.Streaming;
        await streaming.WriteAsync(Encoding.ASCII.GetBytes("partial"));
        await streaming.FlushAsync();

        // Act
        using (StreamReader reader = new(context.Request.Body, leaveOpen: true))
        {
            await Should.ThrowAsync<IOException>(() => reader.ReadToEndAsync());
        }

        // Assert — RFC 9114 §10.5.1: the oversized section is treated as malformed (§4.1.2).
        stream.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.MessageError);
        context.Request.Trailers.Count.ShouldBe(0);
        connection.State.ShouldBe(ConnectionState.Open);

        await connectionContext.SendAsync(context);
    }

    // ------------------------------------------------------------ repeated-field combining

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: A repeated list field should combine in time linear in its repeats")]
    public void BuildRequestHead_OnManyRepeatedListFields_ShouldAllocateLinearly()
    {
        // Arrange — what a raised limit lets through: thousands of repeats of one field.
        List<(string Name, string Value)> fields = CreateRequestFields(Enumerable.Repeat(("accept", "*/*"), manyRepeats));

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        TransportHttpRequestHead head = Http3HeaderCodec.BuildRequestHead(fields, HttpScheme.Https, new HttpTrailerCollection(isSupported: true), out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        head.Headers[HttpHeaderKey.Accept].Count.ShouldBe(manyRepeats);
        allocated.ShouldBeLessThan(linearAllocationBound);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Field Section Limit: Cookie crumbs should join in time linear in their count")]
    public void BuildRequestHead_OnManyCookieCrumbs_ShouldJoinLinearly()
    {
        // Arrange — RFC 9114 §4.2.1: the crumbs of a split cookie field are joined with "; ".
        List<(string Name, string Value)> fields = CreateRequestFields(
            [("x-before", "1"), .. Enumerable.Repeat(("cookie", "a=1"), manyRepeats), ("x-after", "2")]);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        TransportHttpRequestHead head = Http3HeaderCodec.BuildRequestHead(fields, HttpScheme.Https, new HttpTrailerCollection(isSupported: true), out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert — the joined value, in the place the first crumb arrived.
        head.Headers[HttpHeaderKey.Cookie].Value.ShouldBe(string.Join("; ", Enumerable.Repeat("a=1", manyRepeats)));
        head.Headers.Select(static header => header.Key.Value).ShouldBe(["x-before", "cookie", "x-after"]);
        allocated.ShouldBeLessThan(linearAllocationBound);
    }

    // ------------------------------------------------------------ helpers

    private static byte[] CreateStaticFieldSection(int fieldCount)
    {
        // Prefix: Required Insert Count 0, Delta Base 0.
        return [0x00, 0x00, .. Enumerable.Repeat(acceptAllFieldLine, fieldCount)];
    }

    private static byte[] CreateRequestWithStaticFields(string path, int fieldCount)
    {
        // The pseudo-header fields as literals, then fieldCount one-octet static references.
        byte[] pseudoHeaders = HttpProtocolPayloadFactory.CreateHttp3FieldSection(
            (":method", "GET"),
            (":scheme", "https"),
            (":path", path),
            (":authority", "a"));

        return HttpProtocolPayloadFactory.CreateHttp3Frame(
            0x1 /* HEADERS */,
            [.. pseudoHeaders, .. Enumerable.Repeat(acceptAllFieldLine, fieldCount)]);
    }

    private static byte[] CreateRequestWithOversizedTrailers()
    {
        (string Name, string Value)[] trailers = Enumerable.Repeat(("x-t", new string('v', 20)), 10).ToArray();

        return
        [
            .. HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            .. HttpProtocolPayloadFactory.CreateHttp3Frame(0x0 /* DATA */, Encoding.ASCII.GetBytes("hello")),
            .. HttpProtocolPayloadFactory.CreateHttp3HeadersFrame(trailers),
        ];
    }

    private static List<(string Name, string Value)> CreateRequestFields(IEnumerable<(string Name, string Value)> regularFields)
    {
        return
        [
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "a"),
            .. regularFields,
        ];
    }

    private static async Task<List<string>> ReceiveAllPathsAsync(TestMultiplexedConnection connection)
    {
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        List<string> paths = [];

        await using (IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator())
        {
            while (await enumerator.MoveNextAsync().AsTask().WaitAsync(_timeout))
            {
                paths.Add(enumerator.Current.Request.Path.Value);
            }
        }

        return paths;
    }

    private static async Task<IHttpContext> ReceiveSingleAsync(TestMultiplexedConnection connection, Action<Http3ConnectionListenerOptions> configure)
    {
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), configure);

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        return await ReadSingleContextAsync(connectionContext);
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext connectionContext)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }
}
