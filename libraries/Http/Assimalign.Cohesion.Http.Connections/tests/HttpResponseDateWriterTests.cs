using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class HttpResponseDateWriterTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Date Writers: HTTP/1.1 and HPACK should append the cached Date to every final status")]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(304)]
    [InlineData(400)]
    [InlineData(500)]
    public void EncodeFinalHead_WithoutApplicationDate_ShouldAppendCachedDate(int status)
    {
        // Arrange
        HttpResponseDateHeader date = CreateDate();
        HttpHeaderCollection headers = new();

        // Act
        string http1 = Encoding.ASCII.GetString(Http1MessageWriter.EncodeHead(status, headers, date).Span);
        List<(string Name, string Value)> http2 = new HPackDecoder().DecodeFieldLines(
            HPackEncoder.EncodeResponseHeaders(status, headers, date));

        // Assert
        http1.ShouldContain($"\r\nDate: {date.Value}\r\n", Case.Sensitive);
        http2.Where(field => field.Name == "date").ShouldHaveSingleItem().Value.ShouldBe(date.Value);
        headers.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Date Writers: An application Date should be preserved and emitted only once")]
    [InlineData(false)]
    [InlineData(true)]
    public void EncodeFinalHead_WithApplicationDate_ShouldPreserveItsValue(bool sameAsCached)
    {
        // Arrange — an equal value also exercises the cached representation of an existing field.
        HttpResponseDateHeader date = CreateDate();
        string applicationDate = sameAsCached ? date.Value : "Sun, 06 Nov 1994 08:49:37 GMT";
        HttpHeaderCollection headers = new() { [HttpHeaderKey.Date] = applicationDate };

        // Act
        string http1 = Encoding.ASCII.GetString(Http1MessageWriter.EncodeHead(HttpStatusCode.Ok, headers, date).Span);
        List<(string Name, string Value)> http2 = new HPackDecoder().DecodeFieldLines(
            HPackEncoder.EncodeResponseHeaders(HttpStatusCode.Ok, headers, date));

        // Assert
        http1.Split("\r\n").Where(line => line.StartsWith("Date:", StringComparison.Ordinal))
            .ShouldHaveSingleItem().ShouldBe($"Date: {applicationDate}");
        http2.Where(field => field.Name == "date").ShouldHaveSingleItem().Value.ShouldBe(applicationDate);
        headers[HttpHeaderKey.Date].Value.ShouldBe(applicationDate);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Date Writers: HTTP/1.1 and QPACK transport rejections should carry the cached Date")]
    [InlineData(400)]
    [InlineData(421)]
    [InlineData(500)]
    public async Task EncodeRejection_WithAutomaticDate_ShouldAppendCachedDate(int status)
    {
        // Arrange
        HttpResponseDateHeader date = CreateDate();
        using MemoryStream output = new();

        // Act
        await Http1MessageWriter.WriteErrorResponseAsync(output, status, CancellationToken.None, date);
        List<(string Name, string Value)> http3 = QPackFieldSectionDecoder.Decode(
            Http3HeaderCodec.EncodeStatusOnlyResponseHeaders(status, date), long.MaxValue);

        // Assert
        Encoding.ASCII.GetString(output.ToArray()).ShouldContain($"\r\nDate: {date.Value}\r\n", Case.Sensitive);
        http3.Where(field => field.Name == "date").ShouldHaveSingleItem().Value.ShouldBe(date.Value);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Date Writers: Interim responses should omit even an application Date on every protocol")]
    [InlineData(100)]
    [InlineData(103)]
    public async Task EncodeInterimHead_WithApplicationDate_ShouldOmitDate(int status)
    {
        // Arrange
        HttpHeaderCollection headers = new() { [HttpHeaderKey.Date] = "Sun, 06 Nov 1994 08:49:37 GMT" };
        using MemoryStream output = new();

        // Act
        await Http1MessageWriter.WriteInterimResponseAsync(output, status, headers, CancellationToken.None);
        List<(string Name, string Value)> http2 = new HPackDecoder().DecodeFieldLines(
            HPackEncoder.EncodeInterimResponseHeaders(status, headers));
        List<(string Name, string Value)> http3 = QPackFieldSectionDecoder.Decode(
            Http3HeaderCodec.EncodeInterimResponseHeaders(status, headers), long.MaxValue);

        // Assert
        Encoding.ASCII.GetString(output.ToArray()).ShouldNotContain("Date:", Case.Insensitive);
        http2.ShouldNotContain(field => field.Name == "date");
        http3.ShouldNotContain(field => field.Name == "date");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Date Writers: Suppressing an interim Date should retain response field validation")]
    public async Task EncodeInterimHead_WithInvalidDate_ShouldRefuseBeforeWriting()
    {
        // Arrange
        HttpHeaderCollection headers = new() { [HttpHeaderKey.Date] = "invalid\r\nx-injected: true" };
        using MemoryStream output = new();

        // Act / Assert — this safety behavior already held before Date suppression.
        await Should.ThrowAsync<HttpInvalidResponseFieldException>(async () =>
            await Http1MessageWriter.WriteInterimResponseAsync(output, HttpStatusCode.EarlyHints, headers, CancellationToken.None));
        Should.Throw<HttpInvalidResponseFieldException>(() => HPackEncoder.EncodeInterimResponseHeaders(HttpStatusCode.EarlyHints, headers));
        Should.Throw<HttpInvalidResponseFieldException>(() => Http3HeaderCodec.EncodeInterimResponseHeaders(HttpStatusCode.EarlyHints, headers));
        output.Length.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Date Writers: Direct final encoder calls with informational status should not leak Date")]
    [InlineData(100, false)]
    [InlineData(101, false)]
    [InlineData(103, false)]
    [InlineData(100, true)]
    [InlineData(101, true)]
    [InlineData(103, true)]
    public void EncodeFinalHead_WithInformationalStatus_ShouldNotEmitDate(int status, bool applicationDate)
    {
        // Arrange
        HttpResponseDateHeader date = CreateDate();
        HttpHeaderCollection headers = new();
        if (applicationDate)
        {
            headers[HttpHeaderKey.Date] = date.Value;
        }

        // Act
        List<(string Name, string Value)> http2 = new HPackDecoder().DecodeFieldLines(
            HPackEncoder.EncodeResponseHeaders(status, headers, date));
        List<(string Name, string Value)> http3 = QPackFieldSectionDecoder.Decode(
            Http3HeaderCodec.EncodeStatusOnlyResponseHeaders(status, date), long.MaxValue);

        // Assert — HTTP/1.1 already rejects a final 1xx at this layer; the multiplexed callers own that guard.
        Should.Throw<InvalidOperationException>(() => Http1MessageWriter.EncodeHead(status, headers, date));
        http2.ShouldNotContain(field => field.Name == "date");
        http3.ShouldNotContain(field => field.Name == "date");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Date Writers: Cached HPACK and QPACK fields should avoid per-response field encoding allocations")]
    public void EncodeFinalHead_WithCachedDate_ShouldAvoidFieldEncodingAllocations()
    {
        // Arrange — compare identical wire output, so the result array has the same cost in both paths.
        HttpResponseDateHeader date = CreateDate();
        HttpHeaderCollection headers = new() { [HttpHeaderKey.Date] = date.Value };
        (string Name, string Value)[] qpackOrdinary = [(":status", "200"), ("date", date.Value)];
        (string Name, string Value)[] qpackCached = [(":status", "200")];
        HPackEncoder.EncodeResponseHeaders(HttpStatusCode.Ok, headers, date)
            .ShouldBe(HPackEncoder.EncodeResponseHeaders(HttpStatusCode.Ok, headers));
        QPackFieldSectionEncoder.Encode(qpackCached, date.Http3Field)
            .ShouldBe(QPackFieldSectionEncoder.Encode(qpackOrdinary));

        // Act
        long ordinary = MeasureAllocations(() =>
        {
            _ = HPackEncoder.EncodeResponseHeaders(HttpStatusCode.Ok, headers);
            _ = QPackFieldSectionEncoder.Encode(qpackOrdinary);
        });
        long cached = MeasureAllocations(() =>
        {
            _ = HPackEncoder.EncodeResponseHeaders(HttpStatusCode.Ok, headers, date);
            _ = QPackFieldSectionEncoder.Encode(qpackCached, date.Http3Field);
        });

        // Assert — the ordinary HPACK path alone allocates a 128-byte field buffer on each iteration.
        (ordinary - cached).ShouldBeGreaterThanOrEqualTo(128L * 100);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Date Writers: Enabling Date on a header-only response should allocate only its additional output bytes")]
    [InlineData(1, 204)]
    [InlineData(1, 304)]
    [InlineData(2, 204)]
    [InlineData(2, 304)]
    [InlineData(3, 204)]
    [InlineData(3, 304)]
    public async Task EncodeFinalHead_WithAutomaticDate_ShouldOnlyAllocateAdditionalWireStorage(int protocol, int status)
    {
        // Arrange — originate real exchanges so the enabled case includes the transport-seeded Date.
        (long disabled, int disabledLength) = await MeasureHeadAllocationsAsync(protocol, status, addDate: false);
        (long enabled, int enabledLength) = await MeasureHeadAllocationsAsync(protocol, status, addDate: true);

        // Act — HTTP/1.1 returns its existing capacity buffer. HPACK and QPACK return exact-length arrays;
        // measure those arrays separately to include the runtime's object alignment in the wire-byte cost.
        long additionalOutputStorage = protocol == 1 ? 0
            : MeasureAllocations(() => GC.KeepAlive(new byte[enabledLength]))
                - MeasureAllocations(() => GC.KeepAlive(new byte[disabledLength]));

        // Assert — Date adds wire bytes, but no boxed header enumerator or field-encoding scratch space.
        enabledLength.ShouldBeGreaterThan(disabledLength);
        (enabled - disabled).ShouldBe(additionalOutputStorage);
    }

    private static HttpResponseDateHeader CreateDate() => new HttpResponseDateCache(new ManualTimeProvider()).GetCurrent();

    private static async Task<(long Allocated, int EncodedLength)> MeasureHeadAllocationsAsync(int protocol, int status, bool addDate)
    {
        HttpConnectionListenerOptions options = new() { AddDateHeader = addDate, DateTimeProvider = new ManualTimeProvider() };
        byte[] request = protocol switch
        {
            1 => HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n"),
            2 => HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "http", "api.test"),
            _ => HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "api.test"),
        };
        TestConnection transport = new(request);
        switch (protocol)
        {
            case 1: options.UseHttp1(new TestConnectionListener(transport)); break;
            case 2: options.UseHttp2(new TestConnectionListener(transport)); break;
            default: options.UseHttp3(new TestMultiplexedConnectionListener(new TestMultiplexedConnection(transport))); break;
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await using HttpConnectionListener listener = new(options);
        await using IHttpConnection connection = await listener.AcceptOrListenAsync(timeout.Token);
        IHttpConnectionContext connectionContext = await connection.OpenAsync(timeout.Token);
        await using IAsyncEnumerator<IHttpContext> requests = connectionContext.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        (await requests.MoveNextAsync()).ShouldBeTrue();
        await using TransportHttpContext context = (TransportHttpContext)requests.Current;
        context.Response.StatusCode = status;
        context.Response.Headers.Count.ShouldBe(addDate ? 1 : 0);
        context.Response.Headers.ContainsKey(HttpHeaderKey.Date).ShouldBe(addDate);
        Func<ReadOnlyMemory<byte>> encode = protocol switch
        {
            1 => () => Http1MessageWriter.EncodeHead(context.Response.StatusCode, context.Response.Headers, context.Response.GetDateHeader()),
            2 => () => HPackEncoder.EncodeResponseHeaders(context.Response.StatusCode, context.Response.Headers, context.Response.GetDateHeader()),
            _ => () => Http3HeaderCodec.EncodeResponseHeaders((Http3Context)context),
        };
        int length = encode().Length;
        long allocated = MeasureAllocations(() => _ = encode());
        return (allocated, length);
    }

    private static long MeasureAllocations(Action action)
    {
        for (int index = 0; index < 10; index++)
        {
            action();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100; index++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
