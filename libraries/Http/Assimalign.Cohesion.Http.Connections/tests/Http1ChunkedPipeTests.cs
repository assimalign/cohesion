using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Pipe boundaries must not change chunk framing, its limits, or the next request.</summary>
public class Http1ChunkedPipeTests
{
    private const int LineCap = 32;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Pipe: Partial framing should release backpressure below the line cap")]
    public async Task ReadAsync_OnPipePauseThresholdBelowLineSize_ShouldReleasePartialFraming()
    {
        // Arrange — retaining the incomplete line would pause the producer before it can send
        // the CRLF. This also covers a partial trailer and CRs sent independently of their LFs.
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 8, resumeWriterThreshold: 4));
        HttpTrailerCollection trailers = new(true);
        using Http1RequestBodyStream body = CreateBody(pipe.Reader, trailers);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using MemoryStream decoded = new();
        Task read = body.CopyToAsync(decoded, timeout.Token);

        // Act
        foreach (string fragment in new[] { "1;" + new string('a', 20), "\r", "\nx\r", "\n0\r", "\nX-End: va", "lue\r", "\n\r", "\n" })
        {
            await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(fragment), timeout.Token);
        }
        await read.WaitAsync(timeout.Token);

        // Assert
        Encoding.ASCII.GetString(decoded.ToArray()).ShouldBe("x");
        trailers.GetValue(new HttpHeaderKey("X-End")).ShouldBe("value");
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Pipe: Single-octet sequence segments should preserve framing, obs-text and the next request")]
    public async Task ReadAsync_OnSingleOctetSegments_ShouldDecodeAndLeavePipeline()
    {
        // Arrange — CR and LF occupy different sequence segments even within one ReadResult.
        const string wire = "5;ext=\"é\"\r\nhello\r\n0\r\nX-End: é\r\n\r\n";
        CountingInputReader input = new(Encoding.Latin1.GetBytes(wire + "GET /next"), 0, 0, segmentSize: 1);
        HttpTrailerCollection trailers = new(true);
        using Http1RequestBodyStream body = CreateBody(input, trailers);
        using MemoryStream decoded = new();

        // Act
        await body.CopyToAsync(decoded, CancellationToken.None);

        // Assert
        Encoding.ASCII.GetString(decoded.ToArray()).ShouldBe("hello");
        trailers.GetValue(new HttpHeaderKey("X-End")).ShouldBe("é");
        input.Consumed.ShouldBe(Encoding.Latin1.GetByteCount(wire));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Pipe: Split size digits, CRLF and trailers should decode without consuming the next request")]
    public async Task ReadAsync_OnFramingSplitAcrossReads_ShouldDecodeAndLeavePipeline()
    {
        // Arrange
        Pipe pipe = new();
        HttpTrailerCollection trailers = new(true);
        using Http1RequestBodyStream body = CreateBody(pipe.Reader, trailers);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using MemoryStream decoded = new();
        Task read = body.CopyToAsync(decoded, timeout.Token);

        // Act — every framing CRLF is split, the hexadecimal size is split mid-number, and the
        // trailer is split mid-value. Each read must wait for the next write to finish its line.
        foreach (string fragment in new[] { "1", "0;x=y\r", "\nabcdefghijklmnop\r", "\n0\r", "\nX-End: va", "lue\r", "\n\r", "\nGET /next HTTP/1.1\r\nHost: a\r\n\r\n" })
        {
            read.IsCompleted.ShouldBeFalse();
            await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(fragment), timeout.Token);
            if (!fragment.StartsWith("\nGET", StringComparison.Ordinal))
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        await read.WaitAsync(timeout.Token);

        // Assert
        Encoding.ASCII.GetString(decoded.ToArray()).ShouldBe("abcdefghijklmnop");
        trailers.GetValue(new HttpHeaderKey("X-End")).ShouldBe("value");
        ReadResult remaining = await pipe.Reader.ReadAsync(timeout.Token);
        Encoding.ASCII.GetString(remaining.Buffer).ShouldBe("GET /next HTTP/1.1\r\nHost: a\r\n\r\n");
        pipe.Reader.AdvanceTo(remaining.Buffer.End);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Pipe: A framing line at its cap should pass and one octet over should retain its rejection")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReadAsync_OnFramingLineAtCapBoundary_ShouldAcceptOrReject(bool trailer, bool over)
    {
        // Arrange
        string line = (trailer ? "X-End: " : "1;") + new string('a', LineCap - (trailer ? 7 : 2) + (over ? 1 : 0));
        Pipe pipe = new();
        using Http1RequestBodyStream body = CreateBody(pipe.Reader);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using MemoryStream decoded = new();
        Task read = body.CopyToAsync(decoded, timeout.Token);
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes((trailer ? "0\r\n" : "") + line + (over ? "" : "\r")), timeout.Token);
        await Task.Delay(20, timeout.Token);

        // Act — a CR at cap + 1 is allowed to wait for LF; an ordinary octet there must fail
        // immediately, even when the peer has not sent a CRLF or completed the pipe.
        if (!over)
        {
            read.IsCompleted.ShouldBeFalse();
            await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(trailer ? "\n\r\n" : "\nx\r\n0\r\n\r\n"), timeout.Token);
            await read.WaitAsync(timeout.Token);
        }
        else if (trailer)
        {
            Http1LimitExceededException failure = await Should.ThrowAsync<Http1LimitExceededException>(() => read.WaitAsync(timeout.Token));
            failure.StatusCode.ShouldBe(HttpStatusCode.RequestHeaderFieldsTooLarge);
            body.RejectedStatusCode.ShouldBe(HttpStatusCode.RequestHeaderFieldsTooLarge);
        }
        else
        {
            await Should.ThrowAsync<InvalidDataException>(() => read.WaitAsync(timeout.Token));
            body.IsMalformed.ShouldBeTrue();
        }
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Pipe: Reading or draining a chunked body should preserve the pipelined request")]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ReceiveAsync_OnChunkedBodyFollowedByRequest_ShouldKeepBoundary(bool readBody, bool disposeBody)
    {
        // Arrange
        TestConnection transport = new(Encoding.ASCII.GetBytes(
            "POST /first HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5;ext=ok\r\nhello\r\n0\r\nX-End: yes\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: a\r\nConnection: close\r\n\r\n"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(transport));
        await using HttpConnectionListener listener = new(options);
        await using IHttpConnection connection = await listener.AcceptAsync();
        IHttpConnectionContext server = await connection.OpenAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        List<string> paths = new();

        // Act
        await foreach (IHttpContext exchange in server.ReceiveAsync(timeout.Token))
        {
            paths.Add(exchange.Request.Path.Value);
            if (readBody)
            {
                using StreamReader reader = new(exchange.Request.Body);
                (await reader.ReadToEndAsync(timeout.Token)).ShouldBe(paths.Count == 1 ? "hello" : "");
            }
            if (disposeBody) { await exchange.Request.Body.DisposeAsync(); }
            await server.SendAsync(exchange, timeout.Token);
        }

        // Assert
        paths.ShouldBe(new[] { "/first", "/next" });
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Pipe: A stalled partial size or trailer line should retain the body data-rate timeout")]
    [InlineData("1;ext=")]
    [InlineData("0\r\nX-End: partial")]
    public async Task ReadAsync_OnStalledFramingLine_ShouldRejectWithRequestTimeout(string prefix)
    {
        // Arrange
        Pipe pipe = new();
        using Http1RequestBodyStream body = CreateBody(pipe.Reader, rate: new HttpMinDataRate(1_000_000, TimeSpan.FromMilliseconds(100)));
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(prefix));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        // Act
        Http1LimitExceededException failure = await Should.ThrowAsync<Http1LimitExceededException>(
            async () => await body.ReadAsync(new byte[256], timeout.Token));

        // Assert
        failure.StatusCode.ShouldBe(HttpStatusCode.RequestTimeout);
        body.RejectedStatusCode.ShouldBe(HttpStatusCode.RequestTimeout);
        (await body.DrainAsync(timeout.Token)).ShouldBeFalse();
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    private static Http1RequestBodyStream CreateBody(PipeReader input, HttpTrailerCollection? trailers = null, HttpMinDataRate? rate = null)
    {
        Http1ConnectionListenerOptions.Http1Limits limits = new()
        {
            MaxChunkFramingLineSize = LineCap,
            MinRequestBodyDataRate = rate,
        };
        return new Http1RequestBodyStream(input, input.AsStream(leaveOpen: true), Http1RequestBodyFraming.Chunked,
            false, null, limits, TimeProvider.System, CancellationToken.None, trailers ?? new HttpTrailerCollection(true));
    }
}
