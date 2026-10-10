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

/// <summary>
/// The bounds on a chunked HTTP/1.1 request body's framing (#1375). A chunk-size line, chunk
/// extensions included, and each trailer field line are capped by
/// <see cref="Http1ConnectionListenerOptions.Http1Limits.MaxChunkFramingLineSize"/>, the chunk-size
/// lines of the whole body by a framing budget of twice that cap beyond what their chunks' data pays
/// for, and the trailer section by the header section's count and size limits. Without them a peer
/// could make the listener buffer a line that never ends, or read far more framing than data,
/// whether the application read the body or the keep-alive drain did. The body-stream tests feed the
/// stream from a peer that keeps sending and check how far the reader got and what it allocated; the
/// connection tests check what the client is answered.
/// </summary>
public class Http1ChunkedFramingLimitTests
{
    private const int LineCap = 1024;

    // What a breached read may allocate: a constant per octet up to the cap, plus the rejection. A
    // Debug build allocates every async state machine it enters, about 150 octets per octet of a
    // framing line read one octet at a time; a Release build allocates little beyond the line. The
    // unbounded reader buffered the whole run instead: megabytes in these tests, in either build.
    private const long BoundedAllocation = 512 * 1024;

    // A run longer than any cap here, sent by a peer that keeps going.
    private const long EndlessRun = 4 * 1024 * 1024;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: An endless chunk extension should be rejected as malformed at the line cap")]
    public void ReadAsync_OnEndlessChunkExtension_ShouldRejectAsMalformedAtTheLineCap()
    {
        // Arrange
        CountingInputStream peer = new(Encoding.ASCII.GetBytes("1;"), (byte)'a', EndlessRun);
        Http1RequestBodyStream body = CreateChunkedBody(peer);

        // Act
        (Exception? failure, long allocated) = ReadToEnd(body);

        // Assert
        failure.ShouldBeOfType<InvalidDataException>();
        body.IsMalformed.ShouldBeTrue();
        peer.Consumed.ShouldBeLessThanOrEqualTo(LineCap + 1);
        allocated.ShouldBeLessThan(BoundedAllocation, $"allocated {allocated} octets");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: An over-long trailer line should be rejected with 431 at the line cap")]
    public void ReadAsync_OnOverlongTrailerLine_ShouldRejectWithFieldsTooLargeAtTheLineCap()
    {
        // Arrange
        CountingInputStream peer = new(Encoding.ASCII.GetBytes("0\r\nX-Checksum: "), (byte)'a', EndlessRun);
        Http1RequestBodyStream body = CreateChunkedBody(peer);

        // Act
        (Exception? failure, long allocated) = ReadToEnd(body);

        // Assert
        failure.ShouldBeOfType<Http1LimitExceededException>().StatusCode.ShouldBe(HttpStatusCode.RequestHeaderFieldsTooLarge);
        body.RejectedStatusCode.ShouldBe(HttpStatusCode.RequestHeaderFieldsTooLarge);
        peer.Consumed.ShouldBeLessThanOrEqualTo(3 + LineCap + 1);
        allocated.ShouldBeLessThan(BoundedAllocation, $"allocated {allocated} octets");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: More trailer fields than the header count limit should be rejected with 431, repeats counted")]
    public void ReadAsync_OnTooManyTrailerFields_ShouldRejectWithFieldsTooLarge()
    {
        // Arrange — the same name repeated: every field line counts, not every distinct name.
        const string fieldLine = "X-Repeat: v\r\n";
        CountingInputStream peer = new(
            Encoding.ASCII.GetBytes("0\r\n" + string.Concat(Enumerable.Repeat(fieldLine, 5000)) + "\r\n"),
            (byte)'a',
            0);
        Http1RequestBodyStream body = CreateChunkedBody(peer, limits => limits.MaxRequestHeaderCount = 100);

        // Act
        (Exception? failure, long allocated) = ReadToEnd(body);

        // Assert
        failure.ShouldBeOfType<Http1LimitExceededException>().StatusCode.ShouldBe(HttpStatusCode.RequestHeaderFieldsTooLarge);
        peer.Consumed.ShouldBeLessThanOrEqualTo(3 + (101 * fieldLine.Length));
        allocated.ShouldBeLessThan(BoundedAllocation, $"allocated {allocated} octets");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: A trailer section over the header-section size limit should be rejected with 431")]
    public void ReadAsync_OnTrailerSectionOverTotalSize_ShouldRejectWithFieldsTooLarge()
    {
        // Arrange — every line is well under the line cap; together they pass the section's size.
        StringBuilder section = new("0\r\n");
        for (int i = 0; i < 1000; i++)
        {
            section.Append("X-Field-").Append(i).Append(": value\r\n");
        }

        section.Append("\r\n");
        CountingInputStream peer = new(Encoding.ASCII.GetBytes(section.ToString()), (byte)'a', 0);
        Http1RequestBodyStream body = CreateChunkedBody(peer, limits =>
        {
            limits.MaxRequestHeaderCount = 10_000;
            limits.MaxRequestHeadersTotalSize = 256;
        });

        // Act
        (Exception? failure, long allocated) = ReadToEnd(body);

        // Assert
        failure.ShouldBeOfType<Http1LimitExceededException>().StatusCode.ShouldBe(HttpStatusCode.RequestHeaderFieldsTooLarge);
        peer.Consumed.ShouldBeLessThanOrEqualTo(3 + 256 + 2);
        allocated.ShouldBeLessThan(BoundedAllocation, $"allocated {allocated} octets");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: Repeated trailer fields should combine in linear time, in arrival order")]
    public void ReadAsync_OnRepeatedTrailerFields_ShouldCombineInLinearTime()
    {
        // Arrange — combining on each repeat copies the values gathered so far, so what the read
        // allocates grows with the square of the repeats; combining once, it grows linearly. The test
        // compares twice the repeats with once rather than bounding either: a Debug build allocates an
        // async state machine for every octet it reads, which an absolute bound would mostly measure.
        // Doubling the repeats about doubles a linear read and about quadruples a quadratic one. The
        // first read takes the one-time costs (type loading, static state) out of the ratio.
        ReadRepeatedTrailers(500);

        // Act
        (long once, _) = ReadRepeatedTrailers(2500);
        (long twice, HttpHeaderValue combined) = ReadRepeatedTrailers(5000);

        // Assert
        combined.Count.ShouldBe(5000);
        combined[0].ShouldBe("v0");
        combined[4999].ShouldBe("v4999");
        ((double)twice / once).ShouldBeLessThan(2.5, $"allocated {once} octets for 2500 repeats and {twice} for 5000");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: The drain of an unread body should stop at the line cap of an endless chunk extension")]
    public void DrainAsync_OnUnreadBodyWithEndlessChunkExtension_ShouldStopAtTheLineCap()
    {
        // Arrange — the application never read the body; the transport drains it for keep-alive.
        CountingInputStream peer = new(Encoding.ASCII.GetBytes("5;"), (byte)'a', EndlessRun);
        Http1RequestBodyStream body = CreateChunkedBody(peer);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<bool> drain = body.DrainAsync(CancellationToken.None);
        bool completedSynchronously = drain.IsCompleted;
        bool drained = drain.IsCompleted && drain.Result;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        completedSynchronously.ShouldBeTrue();
        drained.ShouldBeFalse();
        body.IsMalformed.ShouldBeTrue();
        peer.Consumed.ShouldBeLessThanOrEqualTo(LineCap + 1);
        allocated.ShouldBeLessThan(BoundedAllocation, $"allocated {allocated} octets");
    }

    /// <summary>
    /// Chunk-size lines just under the line cap: one carrying a chunk extension, and one whose
    /// chunk-size is padded with leading zeros.
    /// </summary>
    public static TheoryData<string> NearCapChunkSizeLines => new()
    {
        "1;" + new string('a', LineCap - 2),
        new string('0', LineCap - 1) + "1",
    };

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: The drain should stop within the framing budget a body whose chunk framing far outweighs its data")]
    [MemberData(nameof(NearCapChunkSizeLines))]
    public void DrainAsync_OnNearCapLineBeforeEveryOneOctetChunk_ShouldStopWithinTheFramingBudget(string sizeLine)
    {
        // Arrange — 2,000 one-octet chunks, each behind a line just under the cap. Every line keeps to
        // the cap and the data keeps to the body-size cap, but the body is about 2 MB of framing for
        // 2,000 octets of data. The budget is twice the line cap and each chunk leaves about one line
        // cap unpaid, so the third line breaks it.
        const int chunks = 2000;
        CountingInputStream peer = new(
            Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(sizeLine + "\r\nx\r\n", chunks)) + "0\r\n\r\n"),
            (byte)'a',
            0);
        Http1RequestBodyStream body = CreateChunkedBody(peer);

        // Act
        ValueTask<bool> drain = body.DrainAsync(CancellationToken.None);
        bool completedSynchronously = drain.IsCompleted;
        bool drained = drain.IsCompleted && drain.Result;

        // Assert — two whole chunks, then the third chunk-size line.
        completedSynchronously.ShouldBeTrue();
        drained.ShouldBeFalse();
        body.IsMalformed.ShouldBeTrue();
        peer.Consumed.ShouldBeLessThanOrEqualTo(3 * (sizeLine.Length + 5));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: Chunk extensions paid for by their chunks' data should be read however many there are")]
    public async Task ReadAsync_OnLongChunkExtensionsPaidForByData_ShouldDeliverTheBody()
    {
        // Arrange — 200 chunks of 512 octets, each behind an extension just under the line cap, about
        // a hundred times the framing budget in all; then 2,000 one-octet chunks behind short
        // extensions. Every line is paid for by its own chunk's data, so none of it accumulates.
        StringBuilder wire = new();
        for (int i = 0; i < 200; i++)
        {
            wire.Append("200;sig=").Append('a', LineCap - 8).Append("\r\n").Append('d', 512).Append("\r\n");
        }

        for (int i = 0; i < 2000; i++)
        {
            wire.Append("1;n=v\r\nd\r\n");
        }

        wire.Append("0\r\n\r\n");
        Http1RequestBodyStream body = CreateChunkedBody(new CountingInputStream(Encoding.ASCII.GetBytes(wire.ToString()), (byte)'a', 0));

        // Act
        long delivered = 0;
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await body.ReadAsync(buffer)) > 0)
        {
            delivered += read;
        }

        // Assert
        delivered.ShouldBe((200 * 512) + 2000);
        body.IsMalformed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: An endless chunk extension read by the application should be answered with 400 and close the connection")]
    public async Task ReceiveAsync_OnEndlessChunkExtension_ShouldAnswerBadRequestAndClose()
    {
        // Arrange — the extension ends, past the cap, and a well-framed request follows.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "1;" + new string('a', 4 * LineCap) + "\r\nx\r\n0\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload, readBody: true);

        // Assert
        result.Paths.ShouldBe(["/upload"]);
        result.ReadFailures.Single().ShouldBeOfType<InvalidDataException>();
        result.Output.ShouldStartWith("HTTP/1.1 400");
        result.Output.ShouldContain("Connection: close");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: An over-long trailer line read by the application should be answered with 431 and close the connection")]
    public async Task ReceiveAsync_OnOverlongTrailerLine_ShouldAnswerFieldsTooLargeAndClose()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5\r\nhello\r\n0\r\nX-Checksum: " + new string('a', 4 * LineCap) + "\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload, readBody: true);

        // Assert
        result.Paths.ShouldBe(["/upload"]);
        result.ReadFailures.Single().ShouldBeAssignableTo<IOException>();
        result.Output.ShouldStartWith("HTTP/1.1 431");
        result.Output.ShouldContain("Connection: close");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: Too many trailer fields read by the application should be answered with 431 and close the connection")]
    public async Task ReceiveAsync_OnTooManyTrailerFields_ShouldAnswerFieldsTooLargeAndClose()
    {
        // Arrange — the default count limit is 100.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5\r\nhello\r\n0\r\n" + string.Concat(Enumerable.Repeat("X-Repeat: v\r\n", 200)) + "\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload, readBody: true);

        // Assert
        result.Paths.ShouldBe(["/upload"]);
        result.ReadFailures.Single().ShouldBeAssignableTo<IOException>();
        result.Output.ShouldStartWith("HTTP/1.1 431");
        result.Output.ShouldContain("Connection: close");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: The keep-alive drain of an unread body should stop at an over-long chunk extension and close the connection")]
    public async Task ReceiveAsync_OnUnreadBodyWithOverlongChunkExtension_ShouldNotDrainPastTheCapAndClose()
    {
        // Arrange — the application answers without reading the body, so the drain reads it.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "1;" + new string('a', 4 * LineCap) + "\r\nx\r\n0\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload, readBody: false);

        // Assert — the response went out before the drain; the connection ends after it.
        result.Paths.ShouldBe(["/upload"]);
        result.Output.ShouldStartWith("HTTP/1.1 200");
        result.Output.Split("HTTP/1.1 ").Length.ShouldBe(2);
    }

    /// <summary>
    /// Reads a body whose trailer section repeats one field <paramref name="repeats"/> times, with the
    /// limits raised to allow it, and returns what the read allocated and the combined field.
    /// </summary>
    private static (long Allocated, HttpHeaderValue Combined) ReadRepeatedTrailers(int repeats)
    {
        StringBuilder section = new("0\r\n");
        for (int i = 0; i < repeats; i++)
        {
            section.Append("X-Repeat: v").Append(i).Append("\r\n");
        }

        section.Append("\r\n");
        CountingInputStream peer = new(Encoding.ASCII.GetBytes(section.ToString()), (byte)'a', 0);
        HttpTrailerCollection trailers = new(isSupported: true);
        Http1RequestBodyStream body = CreateChunkedBody(peer, limits =>
        {
            limits.MaxRequestHeaderCount = repeats;
            limits.MaxRequestHeadersTotalSize = 1024 * 1024;
        }, trailers);

        (Exception? failure, long allocated) = ReadToEnd(body);

        failure.ShouldBeNull();
        return (allocated, trailers[new HttpHeaderKey("x-repeat")]);
    }

    private static Http1RequestBodyStream CreateChunkedBody(
        Stream peer,
        Action<Http1ConnectionListenerOptions.Http1Limits>? configure = null,
        HttpTrailerCollection? trailers = null)
    {
        // No data rate: its per-read timers would be allocations that are not the buffering under test.
        Http1ConnectionListenerOptions.Http1Limits limits = new()
        {
            MaxChunkFramingLineSize = LineCap,
            MinRequestBodyDataRate = null,
        };
        configure?.Invoke(limits);

        return new Http1RequestBodyStream(
            peer,
            Http1RequestBodyFraming.Chunked,
            solicitContinue: false,
            interception: null,
            limits,
            TimeProvider.System,
            CancellationToken.None,
            trailers ?? new HttpTrailerCollection(isSupported: true));
    }

    /// <summary>
    /// Reads the body to its end on the calling thread and returns the read's failure and what the
    /// reads allocated. The peer completes every read synchronously, so no read may go asynchronous.
    /// </summary>
    private static (Exception? Failure, long Allocated) ReadToEnd(Http1RequestBodyStream body)
    {
        byte[] buffer = new byte[256];
        Exception? failure = null;
        long before = GC.GetAllocatedBytesForCurrentThread();

        try
        {
            while (true)
            {
                ValueTask<int> read = body.ReadAsync(buffer);

                if (!read.IsCompleted)
                {
                    throw new InvalidOperationException("A body read went asynchronous against a synchronous peer.");
                }

                if (read.Result == 0)
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        return (failure, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// Serves every request on one connection with a <see cref="LineCap"/> framing-line cap: reads
    /// each body to its end when <paramref name="readBody"/> is set, then sends the response. Returns
    /// the paths served, each body read's failure, and everything the server wrote.
    /// </summary>
    private static async Task<Served> ServeAsync(byte[] payload, bool readBody)
    {
        TestConnection transport = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(transport), http1 => http1.Limits.MaxChunkFramingLineSize = LineCap);
        Served served = new();

        await using (HttpConnectionListener listener = new(options))
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync();
            IHttpConnectionContext connectionContext = await connection.OpenAsync();

            await foreach (IHttpContext exchange in connectionContext.ReceiveAsync())
            {
                served.Paths.Add(exchange.Request.Path.Value);

                if (readBody)
                {
                    served.ReadFailures.Add(await TryReadToEndAsync(exchange.Request.Body));
                }

                await connectionContext.SendAsync(exchange);
            }
        }

        // The connection is disposed, so its output is complete: everything the server wrote is here.
        served.Output = Encoding.ASCII.GetString(await transport.ReadOutputAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        return served;
    }

    private static async Task<Exception?> TryReadToEndAsync(Stream body)
    {
        try
        {
            byte[] buffer = new byte[256];
            while (await body.ReadAsync(buffer) > 0)
            {
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class Served
    {
        public List<string> Paths { get; } = new();

        public List<Exception?> ReadFailures { get; } = new();

        public string Output { get; set; } = string.Empty;
    }
}
