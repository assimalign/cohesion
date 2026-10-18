using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Pins the request-head reader's line boundaries now that it scans the connection pipe's buffered
/// octets for each CRLF instead of reading one octet at a time: a line may hold exactly its cap, a
/// CRLF may arrive split across reads, and a CR at the cap waits for the LF that may follow it.
/// </summary>
public class Http1RequestHeadReaderTests
{
    private const int RequestLineCap = 32;

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Head Reader: A request line of exactly the cap should be served and one octet over answered with 414")]
    [InlineData(RequestLineCap, true)]
    [InlineData(RequestLineCap + 1, false)]
    public async Task Http1_OnRequestLineAtCapBoundary_ShouldServeOrReject(int lineLength, bool served)
    {
        // "GET /" + target + " HTTP/1.1" is 14 octets around the target.
        string requestLine = "GET /" + new string('a', lineLength - 14) + " HTTP/1.1";
        requestLine.Length.ShouldBe(lineLength);

        TestConnection connection = new(Encoding.ASCII.GetBytes(requestLine + "\r\nHost: api.test\r\n\r\n"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection), http1 => http1.Limits.MaxRequestLineSize = RequestLineCap);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();

        bool yielded = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        yielded.ShouldBe(served);
        if (!served)
        {
            string response = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
            response.ShouldContain("414", Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Head Reader: A head whose CR and LF arrive in separate reads should be parsed")]
    public async Task Http1_OnCrlfSplitAcrossReads_ShouldParseHead()
    {
        TestConnection connection = new(completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        Task<bool> next = enumerator.MoveNextAsync().AsTask();

        // Every CR ends one write and its LF starts the next, so no read sees a whole CRLF until the
        // following write lands; the reader must keep the CR it already examined.
        foreach (string fragment in new[] { "GET /split HTTP/1.1\r", "\nHost: api.test\r", "\nX-Trace: abc\r", "\n\r", "\n" })
        {
            await connection.WriteInputAsync(Encoding.ASCII.GetBytes(fragment));
            await Task.Delay(20);
        }

        (await next.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/split");
        enumerator.Current.Request.Headers.GetValue(HttpHeaderKey.Host).ShouldBe("api.test");
        enumerator.Current.Request.Headers.GetValue(new HttpHeaderKey("X-Trace")).ShouldBe("abc");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Head Reader: A CR at the cap should wait for its LF rather than answer 414")]
    public async Task Http1_OnCarriageReturnAtCap_ShouldWaitForLineFeed()
    {
        // The line holds exactly the cap. Its CR arrives alone at octet cap + 1, which is not yet proof
        // that the line is too long: only the octet after it decides.
        string requestLine = "GET /" + new string('a', RequestLineCap - 14) + " HTTP/1.1";
        TestConnection connection = new(completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection), http1 => http1.Limits.MaxRequestLineSize = RequestLineCap);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        Task<bool> next = enumerator.MoveNextAsync().AsTask();

        await connection.WriteInputAsync(Encoding.ASCII.GetBytes(requestLine + "\r"));
        await Task.Delay(50);
        next.IsCompleted.ShouldBeFalse();

        await connection.WriteInputAsync(Encoding.ASCII.GetBytes("\nHost: api.test\r\n\r\n"));

        (await next.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/" + new string('a', RequestLineCap - 14));
    }
}
