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

    private static HttpResponseDateHeader CreateDate() => new HttpResponseDateCache(new ManualTimeProvider()).GetCurrent();

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
