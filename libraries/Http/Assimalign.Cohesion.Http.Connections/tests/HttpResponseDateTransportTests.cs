using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class HttpResponseDateTransportTests
{
    private const string initialDate = "Thu, 01 Jan 2026 00:00:00 GMT";
    private const string customDate = "Sun, 06 Nov 1994 08:49:37 GMT";

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Date: All transports should preserve their response date policy on buffered and streamed final responses")]
    [InlineData(1, "default")]
    [InlineData(1, "custom")]
    [InlineData(1, "disabled")]
    [InlineData(1, "disabled-custom")]
    [InlineData(1, "cleared")]
    [InlineData(1, "streamed")]
    [InlineData(1, "streamed-disabled")]
    [InlineData(1, "streamed-custom")]
    [InlineData(2, "default")]
    [InlineData(2, "custom")]
    [InlineData(2, "disabled")]
    [InlineData(2, "disabled-custom")]
    [InlineData(2, "cleared")]
    [InlineData(2, "streamed")]
    [InlineData(2, "streamed-disabled")]
    [InlineData(2, "streamed-custom")]
    [InlineData(3, "default")]
    [InlineData(3, "custom")]
    [InlineData(3, "disabled")]
    [InlineData(3, "disabled-custom")]
    [InlineData(3, "cleared")]
    [InlineData(3, "streamed")]
    [InlineData(3, "streamed-disabled")]
    [InlineData(3, "streamed-custom")]
    public async Task SendAsync_OnDatePolicy_ShouldWriteExpectedDate(int protocol, string mode)
    {
        // Arrange
        ManualTimeProvider clock = new();
        bool enabled = !mode.Contains("disabled", StringComparison.Ordinal);
        bool custom = mode.Contains("custom", StringComparison.Ordinal);
        bool streamed = mode.StartsWith("streamed", StringComparison.Ordinal);
        HttpConnectionListenerOptions options = new() { AddDateHeader = enabled, DateTimeProvider = clock };
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
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
        IHttpContext context = requests.Current;
        context.Response.Headers.ContainsKey(HttpHeaderKey.Date).ShouldBe(enabled);
        if (enabled)
        {
            context.Response.Headers[HttpHeaderKey.Date].Value.ShouldBe(initialDate);
        }
        if (custom)
        {
            context.Response.Headers[HttpHeaderKey.Date] = customDate;
        }
        if (mode == "cleared")
        {
            context.Response.Headers.Clear();
        }
        clock.Advance(TimeSpan.FromSeconds(2));
        context.Response.StatusCode = HttpStatusCode.InternalServerError;

        // Act
        if (streamed)
        {
            await context.Response.Streaming.WriteAsync("body"u8.ToArray(), timeout.Token);
        }
        else
        {
            context.Response.Body = new MemoryStream("body"u8.ToArray());
        }
        await connectionContext.SendAsync(context, timeout.Token);
        byte[] output = await transport.ReadOutputAsync().WaitAsync(timeout.Token);
        Dictionary<string, string> headers = ReadHeaders(protocol, output);

        // Assert — an origination Date survives a slow handler; a cleared head is regenerated at send.
        if (enabled || custom)
        {
            string expected = custom ? customDate : mode == "cleared" ? "Thu, 01 Jan 2026 00:00:02 GMT" : initialDate;
            headers["date"].ShouldBe(expected);
        }
        else
        {
            headers.ContainsKey("date").ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Date cache: One provider should share one formatted and encoded value per second without hot-path allocations")]
    public void GetCurrent_WithinSecond_ShouldReuseSnapshotWithoutAllocations()
    {
        // Arrange
        ManualTimeProvider clock = new();
        HttpResponseDateCache cache = HttpResponseDateCache.For(clock);
        HttpResponseDateHeader first = cache.GetCurrent();
        for (int i = 0; i < 100; i++)
        {
            _ = cache.GetCurrent();
        }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        HttpResponseDateHeader? current = null;
        for (int i = 0; i < 10000; i++)
        {
            current = cache.GetCurrent();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocated.ShouldBe(0);
        current.ShouldBeSameAs(first);
        HttpResponseDateCache.For(clock).ShouldBeSameAs(cache);
        first.Value.ShouldBe(initialDate);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Date cache: Second boundaries and clock rollback should refresh all encodings together")]
    public void GetCurrent_OnClockChange_ShouldRefreshCoherentSnapshot()
    {
        // Arrange
        ManualTimeProvider clock = new();
        HttpResponseDateCache cache = new(clock);
        HttpResponseDateHeader first = cache.GetCurrent();

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(999));
        HttpResponseDateHeader same = cache.GetCurrent();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        HttpResponseDateHeader next = cache.GetCurrent();
        clock.Advance(TimeSpan.FromSeconds(-1));
        HttpResponseDateHeader rolledBack = cache.GetCurrent();

        // Assert
        same.ShouldBeSameAs(first);
        next.ShouldNotBeSameAs(first);
        next.Value.ShouldBe("Thu, 01 Jan 2026 00:00:01 GMT");
        Encoding.ASCII.GetString(next.Http1Field.Span).ShouldBe("Date: " + next.Value + "\r\n");
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(next.Http2Field.ToArray())["date"].ShouldBe(next.Value);
        byte[] qpack = new byte[next.Http3Field.Length + 2];
        next.Http3Field.CopyTo(qpack.AsMemory(2));
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(qpack)["date"].ShouldBe(next.Value);
        rolledBack.Value.ShouldBe(first.Value);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Date: Predispatch status responses should honor the listener Date policy")]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    public async Task ReceiveAsync_OnBodyLimitRejection_ShouldHonorDatePolicy(int protocol, bool enabled)
    {
        // Arrange — a request hook forces lazy bodies through the cap before application dispatch.
        HttpConnectionListenerOptions options = new() { AddDateHeader = enabled, DateTimeProvider = new ManualTimeProvider() };
        if (protocol != 2)
        {
            options.Interceptors.Add(new EagerBodyReadingInterceptor());
        }
        Dictionary<string, string> fields = new() { ["content-length"] = "2" };
        byte[] request = protocol switch
        {
            1 => HttpProtocolPayloadFactory.CreateHttp1Request("POST / HTTP/1.1\r\nHost: api.test\r\nContent-Length: 2\r\n\r\nab"),
            2 => HttpProtocolPayloadFactory.CreateHttp2Request(1, "POST", "/", "http", "api.test", fields, "ab"u8.ToArray()),
            _ => HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/", "https", "api.test", fields, "ab"u8.ToArray()),
        };
        TestConnection transport = new(request);
        switch (protocol)
        {
            case 1: options.UseHttp1(new TestConnectionListener(transport), http => http.Limits.MaxRequestBodySize = 1); break;
            case 2: options.UseHttp2(new TestConnectionListener(transport), http => http.Limits.MaxRequestBodySize = 1); break;
            default: options.UseHttp3(new TestMultiplexedConnectionListener(new TestMultiplexedConnection(transport)), http => http.Limits.MaxRequestBodySize = 1); break;
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await using HttpConnectionListener listener = new(options);
        await using IHttpConnection connection = await listener.AcceptOrListenAsync(timeout.Token);
        IHttpConnectionContext connectionContext = await connection.OpenAsync(timeout.Token);
        int dispatched = 0;
        async Task ReceiveAsync()
        {
            try
            {
                await foreach (IHttpContext context in connectionContext.ReceiveAsync(timeout.Token))
                {
                    dispatched++;
                    await context.DisposeAsync();
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }

        // Act
        Task receiving = ReceiveAsync();
        using MemoryStream wire = new();
        Dictionary<string, string> headers;
        try
        {
            while (true)
            {
                wire.Write(await transport.ReadOutputAsync().WaitAsync(timeout.Token));
                byte[] bytes = wire.ToArray();
                bool complete = protocol switch
                {
                    1 => Encoding.ASCII.GetString(bytes).Contains("\r\n\r\n", StringComparison.Ordinal),
                    2 => HttpProtocolPayloadFactory.ParseHttp2Frames(bytes).Any(frame => frame.FrameType == 1),
                    _ => HttpProtocolPayloadFactory.ParseHttp3Frames(bytes).Any(frame => frame.FrameType == 1),
                };
                if (complete)
                {
                    headers = ReadHeaders(protocol, bytes);
                    break;
                }
            }
        }
        finally
        {
            await timeout.CancelAsync();
            await receiving;
        }

        // Assert
        dispatched.ShouldBe(0);
        if (protocol == 1)
        {
            Encoding.ASCII.GetString(wire.ToArray()).ShouldStartWith("HTTP/1.1 413");
        }
        else
        {
            headers[":status"].ShouldBe("413");
        }
        headers["content-length"].ShouldBe("0");
        headers.ContainsKey("date").ShouldBe(enabled);
        if (enabled)
        {
            headers["date"].ShouldBe(initialDate);
        }
    }

    private sealed class EagerBodyReadingInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
        {
            // HTTP/1.1 and HTTP/3 enforce their cap on the first body read.
            body.CopyTo(Stream.Null);
            return body;
        }
    }
    private static Dictionary<string, string> ReadHeaders(int protocol, byte[] output)
    {
        if (protocol == 2)
        {
            return HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(HttpProtocolPayloadFactory.ParseHttp2Frames(output).First(frame => frame.FrameType == 1).Payload);
        }
        if (protocol == 3)
        {
            return HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(HttpProtocolPayloadFactory.ParseHttp3Frames(output).First(frame => frame.FrameType == 1).Payload);
        }
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in Encoding.ASCII.GetString(output).Split("\r\n").Skip(1))
        {
            if (line.Length == 0)
            {
                break;
            }
            int separator = line.IndexOf(':');
            headers.Add(line[..separator], line[(separator + 1)..].Trim());
        }
        return headers;
    }
}
