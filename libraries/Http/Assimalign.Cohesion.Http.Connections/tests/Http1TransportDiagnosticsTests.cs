using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
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
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Http.Connections.Tests;

[Collection(HttpTransportEventSourceTests.CollectionName)]
public class Http1TransportDiagnosticsTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - HTTP/1 transport rejections publish the status without request text")]
    [InlineData(400, "GET /secret-target HTTP/1.1\r\nHost: private.invalid\r\nBad Name: secret-value\r\n\r\n")]
    [InlineData(414, "GET /secret-target-is-too-long HTTP/1.1\r\nHost: private.invalid\r\n\r\n")]
    [InlineData(421, "GET https://private.invalid/secret-target HTTP/1.1\r\nHost: private.invalid\r\n\r\n")]
    [InlineData(431, "GET / HTTP/1.1\r\nHost: private.invalid\r\nExtra: secret-value\r\n\r\n")]
    [InlineData(501, "LONGUNKNOWNMETHODLONGUNKNOWNMETHODLONGUNKNOWNMETHOD / HTTP/1.1\r\nHost: private.invalid\r\n\r\n")]
    public async Task ReceiveAsync_OnRejectedHead_ShouldPublishBothSignals(int statusCode, string request)
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using EventSourceRecorder events = new(HttpTransportEventSource.Log, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using TestConnection driver = new(Encoding.ASCII.GetBytes(request));
        Http1ConnectionListenerOptions.Http1Limits limits = new();
        if (statusCode == 414)
        {
            limits.MaxRequestLineSize = 20;
        }
        if (statusCode == 431)
        {
            limits.MaxRequestHeaderCount = 1;
        }
        Http1ConnectionContext connection = Create(driver, limits);
        long before = meter.Count("cohesion.http.server.rejected_requests", "1.1", statusCode);

        // Act
        await using IAsyncEnumerator<IHttpContext> receive = connection.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        (await receive.MoveNextAsync()).ShouldBeFalse();

        // Assert
        meter.Count("cohesion.http.server.rejected_requests", "1.1", statusCode).ShouldBe(before + 1);
        EventWrittenEventArgs rejected = SingleEvent(events, 1, driver);
        Payload(rejected, "statusCode").ShouldBe(statusCode);
        Payload(rejected, "streamId").ShouldBe(-1L);
        rejected.Payload!.OfType<string>().ShouldNotContain(value => value.Contains("secret", StringComparison.Ordinal)
            || value.Contains("private.invalid", StringComparison.Ordinal));
        Encoding.ASCII.GetString(await driver.ReadOutputAsync()).ShouldStartWith($"HTTP/1.1 {statusCode}");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - HTTP/1 body fault reports once before send and on repeated reads")]
    [InlineData(400, "Transfer-Encoding: chunked\r\n", "invalid-chunk\r\n")]
    [InlineData(408, "Content-Length: 5\r\n", "")]
    [InlineData(413, "Content-Length: 5\r\n", "abcde")]
    [InlineData(431, "Transfer-Encoding: chunked\r\n", "0\r\nOne: first\r\nTwo: second\r\nThree: third\r\n\r\n")]
    public async Task ReadAsync_OnClientFault_ShouldReportOnceAtLatch(int statusCode, string framing, string body)
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using EventSourceRecorder events = new(HttpTransportEventSource.Log, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using TestConnection driver = new(Encoding.ASCII.GetBytes("POST /secret-target HTTP/1.1\r\nHost: private.invalid\r\n" + framing + "\r\n" + body), completeInput: false);
        Http1ConnectionListenerOptions.Http1Limits limits = new()
        {
            MinRequestBodyDataRate = new HttpMinDataRate(1_000_000, TimeSpan.FromMilliseconds(100)),
            MaxRequestBodySize = statusCode == 413 ? 1 : 100,
            MaxRequestHeaderCount = 2,
        };
        Http1ConnectionContext connection = Create(driver, limits);
        await using IAsyncEnumerator<IHttpContext> receive = connection.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        (await receive.MoveNextAsync()).ShouldBeTrue();
        await using IHttpContext exchange = receive.Current;
        long before = meter.Count("cohesion.http.server.rejected_requests", "1.1", statusCode);

        // Act
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Exception? failure = await Record.ExceptionAsync(() => exchange.Request.Body.ReadAsync(new byte[16], CancellationToken.None).AsTask());
            failure.ShouldNotBeNull();
        }

        // Assert — observable before a host's final send, which must not count it again.
        meter.Count("cohesion.http.server.rejected_requests", "1.1", statusCode).ShouldBe(before + 1);
        Payload(SingleEvent(events, 1, driver), "statusCode").ShouldBe(statusCode);
        await connection.SendAsync(exchange, CancellationToken.None);
        meter.Count("cohesion.http.server.rejected_requests", "1.1", statusCode).ShouldBe(before + 1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/1 slow reader publishes its rate abort and first client outcome")]
    public async Task SendAsync_OnSlowReader_ShouldPublishBothSignals()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using EventSourceRecorder events = new(HttpTransportEventSource.Log, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using TestConnection inner = new("GET / HTTP/1.1\r\nHost: private.invalid\r\n\r\n"u8.ToArray());
        await using StallingConnection driver = new(inner);
        Http1ConnectionContext connection = Create(driver, new() { MinResponseDataRate = new HttpMinDataRate(1_000_000, TimeSpan.FromMilliseconds(100)) });
        await using IAsyncEnumerator<IHttpContext> receive = connection.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        (await receive.MoveNextAsync()).ShouldBeTrue();
        await using IHttpContext exchange = receive.Current;
        exchange.Response.Body = new MemoryStream(new byte[1024]);
        driver.Writer.Stall();
        long rateBefore = meter.Count("cohesion.http.server.response_rate_aborts", "1.1");
        long abortBefore = meter.Count("cohesion.http.server.response_aborts", "1.1");

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => connection.SendAsync(exchange, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        ((Http1Context)exchange).LatchResponseAbortedByClient();

        // Assert
        meter.Count("cohesion.http.server.response_rate_aborts", "1.1").ShouldBe(rateBefore + 1);
        meter.Count("cohesion.http.server.response_aborts", "1.1").ShouldBe(abortBefore + 1);
        SingleEvent(events, 4, driver);
        SingleEvent(events, 5, driver);
        driver.IsAborted.ShouldBeTrue();
    }

    private static Http1ConnectionContext Create(IDuplexConnection driver, Http1ConnectionListenerOptions.Http1Limits limits)
        => new(driver, false, limits, [], [], 0, null);

    private static EventWrittenEventArgs SingleEvent(EventSourceRecorder events, int id, IDuplexConnection driver)
    {
        events.Events.ShouldNotContain(item => item.EventId == 0);
        EventWrittenEventArgs[] matches = events.Events.Where(item => item.EventId == id
            && Equals(Payload(item, "connectionId"), driver.Id.ToString())).ToArray();
        matches.Length.ShouldBe(1);
        return matches[0];
    }

    private static object? Payload(EventWrittenEventArgs item, string name)
        => item.Payload![item.PayloadNames!.IndexOf(name)];
}
