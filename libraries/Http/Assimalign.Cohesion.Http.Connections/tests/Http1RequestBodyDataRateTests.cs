using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Integration tests for the streamed HTTP/1.1 request body: the minimum data-rate defence
/// (<see cref="HttpMinDataRate"/>) against a slow / trickling sender, and the happy paths where a
/// body that keeps pace (or has the rate disabled) is delivered intact.
/// </summary>
public class Http1RequestBodyDataRateTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 DataRate: Should reclaim a request whose body trickles below the minimum rate")]
    public async Task Http1_OnSlowRequestBody_ShouldTimeOut()
    {
        // The peer sends the head and declares a 100-octet body, then never delivers it. With a short
        // grace period the streamed body read is reclaimed (RFC 9110 §15.5.9 timeout semantics)
        // instead of blocking on a stalled sender indefinitely.
        byte[] head = Encoding.ASCII.GetBytes(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 100\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        TestConnection connection = new(head, completeInput: false);
        options.UseHttp1(new TestConnectionListener(connection), http1 =>
            http1.Limits.MinRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromMilliseconds(100)));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(context);

        byte[] buffer = new byte[100];
        await Should.ThrowAsync<IOException>(async () =>
            await httpContext.Request.Body.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 DataRate: Should deliver a body that keeps pace with the minimum rate")]
    public async Task Http1_OnBodyMeetingRate_ShouldDeliverBodyIntact()
    {
        // The whole body is already on the wire, so every read completes immediately and the rate is
        // never at risk. A configured data rate must not interfere with a healthy transfer.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
        HttpConnectionListenerOptions options = new();
        TestConnection connection = new(payload);
        options.UseHttp1(new TestConnectionListener(connection), http1 =>
            http1.Limits.MinRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 1, gracePeriod: TimeSpan.FromSeconds(30)));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(context);

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 DataRate: A null data rate should not gate the body read")]
    public async Task Http1_WithNullDataRate_ShouldDeliverBodyIntact()
    {
        // Disabling the data rate takes the ungated read path; the body is still delivered.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
        HttpConnectionListenerOptions options = new();
        TestConnection connection = new(payload);
        options.UseHttp1(new TestConnectionListener(connection), http1 => http1.Limits.MinRequestBodyDataRate = null);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(context);

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 DataRate: A Content-Length of zero should read as an already-empty body")]
    public async Task Http1_WithZeroContentLength_ShouldReadAsEmptyBody()
    {
        // A `Content-Length: 0` body is born fully read. Before the fix, the first read issued a
        // zero-length wire read that blocked for octets the peer never sends until the data-rate
        // gate reclaimed the exchange — with an aggressive rate configured, this read must still
        // complete immediately and empty.
        byte[] head = Encoding.ASCII.GetBytes(
            "QUERY /search HTTP/1.1\r\nHost: api.test\r\nContent-Length: 0\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        TestConnection connection = new(head, completeInput: false);
        options.UseHttp1(new TestConnectionListener(connection), http1 =>
            http1.Limits.MinRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromMilliseconds(100)));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(context);

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(string.Empty);
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }
}
