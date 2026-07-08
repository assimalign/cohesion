using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class Http1ServerLimitsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Limits: Should reject an over-long request line with 414 and drop the connection")]
    public async Task Http1_OnRequestLineExceedingLimit_ShouldRespond414AndDrop()
    {
        // RFC 9110 §15.5.15 — a request line larger than the configured cap is 414 URI Too Long.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /an-intentionally-very-long-request-target-that-blows-the-cap HTTP/1.1\r\nHost: api.test\r\n\r\n");
        (bool yielded, string response) = await DriveAsync(payload, http1 => http1.Limits.MaxRequestLineSize = 24);

        yielded.ShouldBeFalse();
        response.ShouldContain("414", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Limits: Should reject too many header fields with 431 and drop the connection")]
    public async Task Http1_OnHeaderCountExceedingLimit_ShouldRespond431AndDrop()
    {
        // RFC 9110 §15.5.22 — more header fields than the configured maximum is 431.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nA: 1\r\nB: 2\r\nC: 3\r\nD: 4\r\n\r\n");
        (bool yielded, string response) = await DriveAsync(payload, http1 => http1.Limits.MaxRequestHeaderCount = 2);

        yielded.ShouldBeFalse();
        response.ShouldContain("431", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Limits: Should reject an oversize header section with 431 and drop the connection")]
    public async Task Http1_OnHeaderSectionExceedingTotalSize_ShouldRespond431AndDrop()
    {
        // RFC 9110 §15.5.22 — the combined header section is size-bounded independently of the
        // header count.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nX-Large: " + new string('v', 512) + "\r\n\r\n");
        (bool yielded, string response) = await DriveAsync(payload, http1 => http1.Limits.MaxRequestHeadersTotalSize = 64);

        yielded.ShouldBeFalse();
        response.ShouldContain("431", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Limits: Should reject a Content-Length body over the cap with 413 and drop the connection")]
    public async Task Http1_OnContentLengthBodyExceedingLimit_ShouldRespond413AndDrop()
    {
        // RFC 9110 §15.5.14 — a declared Content-Length over the configured cap is 413.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 4096\r\n\r\n");
        (bool yielded, string response) = await DriveAsync(payload, http1 => http1.Limits.MaxRequestBodySize = 16);

        yielded.ShouldBeFalse();
        response.ShouldContain("413", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Limits: Should reject a chunked body over the cap with 413 and drop the connection")]
    public async Task Http1_OnChunkedBodyExceedingLimit_ShouldRespond413AndDrop()
    {
        // RFC 9110 §15.5.14 — a chunked body that accumulates past the cap is 413.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "8\r\nabcdefgh\r\n"
            + "0\r\n\r\n");
        (bool yielded, string response) = await DriveAsync(payload, http1 => http1.Limits.MaxRequestBodySize = 4);

        yielded.ShouldBeFalse();
        response.ShouldContain("413", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Limits: Should accept a request within the configured limits")]
    public async Task Http1_OnRequestWithinLimits_ShouldParse()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /widgets HTTP/1.1\r\nHost: api.test\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        TestConnection connection = new(payload);
        options.UseHttp1(new TestConnectionListener(connection), http1 =>
        {
            http1.Limits.MaxRequestLineSize = 256;
            http1.Limits.MaxRequestHeaderCount = 10;
            http1.Limits.MaxRequestHeadersTotalSize = 4096;
        });

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(context);

        httpContext.Request.Method.ShouldBe(HttpMethod.Get);
        httpContext.Request.Path.Value.ShouldBe("/widgets");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Timeouts: Should reclaim a slow-header (Slowloris) connection with 408")]
    public async Task Http1_OnSlowHeaders_ShouldRespond408AndDrop()
    {
        // A peer sends the request line then stalls before completing the header section. Once the
        // request-headers deadline elapses the connection is reclaimed with 408 Request Timeout.
        byte[] partialHead = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n");
        HttpConnectionListenerOptions options = new();
        TestConnection connection = new(partialHead, completeInput: false);
        options.UseHttp1(new TestConnectionListener(connection), http1 =>
        {
            http1.Limits.RequestHeadersTimeout = TimeSpan.FromMilliseconds(150);
            http1.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();

        bool yielded = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        yielded.ShouldBeFalse();
        string response = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
        response.ShouldContain("408", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Timeouts: Should reclaim an idle keep-alive connection after the first request")]
    public async Task Http1_OnIdleKeepAlive_ShouldReclaimConnection()
    {
        // The first request completes and keep-alive is in effect; the peer then goes idle. Once
        // the keep-alive deadline elapses the connection is reclaimed (no response is emitted).
        byte[] firstRequest = Encoding.ASCII.GetBytes("GET /one HTTP/1.1\r\nHost: api.test\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        TestConnection connection = new(firstRequest, completeInput: false);
        options.UseHttp1(new TestConnectionListener(connection), http1 =>
        {
            http1.Limits.KeepAliveTimeout = TimeSpan.FromMilliseconds(150);
            http1.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        });

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();

        // First request parses normally.
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/one");

        // Idle keep-alive: the next request never arrives, so the connection is reclaimed.
        bool secondYielded = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        secondYielded.ShouldBeFalse();
    }

    private static async Task<(bool Yielded, string Response)> DriveAsync(byte[] payload, Action<Http1ConnectionListenerOptions> configure)
    {
        HttpConnectionListenerOptions options = new();
        TestConnection connection = new(payload);
        options.UseHttp1(new TestConnectionListener(connection), configure);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();

        bool yielded;
        await using (IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator())
        {
            yielded = await enumerator.MoveNextAsync();
        }

        string response = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
        return (yielded, response);
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }
}
