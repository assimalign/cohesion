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

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Http.Connections.Tests;

[Collection(HttpTransportEventSourceTests.CollectionName)]
public class Http1PreDispatchDiagnosticsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/1 body fault caught before dispatch reports once when the exchange acquires its body")]
    public async Task ReceiveAsync_OnBodyFaultCaughtByInterceptor_ShouldReportOnceBeforeFinalSend()
    {
        // Arrange — the body hook encounters the already-buffered malformed chunk before an exchange
        // exists, catches it, and returns the same body. Its client-fault latch still decides the response.
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder events = new(HttpTransportEventSource.Log, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using TestConnection driver = new(
            "POST /private-target HTTP/1.1\r\nHost: secret.invalid\r\nTransfer-Encoding: chunked\r\n\r\ninvalid-chunk\r\n"u8.ToArray());
        CatchingBodyInterceptor interceptor = new();
        Http1ConnectionContext connection = new(driver, false, new(), [interceptor], [], 0, null);
        long before = meter.Count("cohesion.http.server.rejected_requests", "1.1", 400);
        long counterBefore = HttpTransportEventSource.Log.RejectedRequests;
        await using IAsyncEnumerator<IHttpContext> receive = connection.ReceiveAsync(timeout.Token).GetAsyncEnumerator();

        // Act
        (await receive.MoveNextAsync()).ShouldBeTrue();
        await using IHttpContext exchange = receive.Current;

        // Assert — SetOwner reports the existing latch before SendAsync, which must not count it again.
        interceptor.CaughtMalformedBody.ShouldBeTrue();
        meter.Count("cohesion.http.server.rejected_requests", "1.1", 400).ShouldBe(before + 1);
        HttpTransportEventSource.Log.RejectedRequests.ShouldBe(counterBefore + 1);
        EventWrittenEventArgs[] rejected = events.Events.Where(item => item.EventId == 1
            && Equals(Payload(item, "connectionId"), driver.Id.ToString())).ToArray();
        rejected.Length.ShouldBe(1);
        Payload(rejected[0], "statusCode").ShouldBe(400);
        Payload(rejected[0], "streamId").ShouldBe(-1L);
        await connection.SendAsync(exchange, timeout.Token);
        meter.Count("cohesion.http.server.rejected_requests", "1.1", 400).ShouldBe(before + 1);
        HttpTransportEventSource.Log.RejectedRequests.ShouldBe(counterBefore + 1);
        events.Events.Count(item => item.EventId == 1
            && Equals(Payload(item, "connectionId"), driver.Id.ToString())).ShouldBe(1);
        events.Events.ShouldNotContain(item => item.EventId == 0);
        Encoding.ASCII.GetString(await driver.ReadOutputAsync().WaitAsync(timeout.Token)).ShouldStartWith("HTTP/1.1 400");
    }

    private static object? Payload(EventWrittenEventArgs item, string name)
        => item.Payload![item.PayloadNames!.IndexOf(name)];

    private sealed class CatchingBodyInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public bool CaughtMalformedBody { get; private set; }

        public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
        {
            // All bytes are already buffered: no pending read is allowed to block the parse hook.
            ValueTask<int> read = body.ReadAsync(new byte[1], CancellationToken.None);
            read.IsCompleted.ShouldBeTrue();
            try
            {
                read.GetAwaiter().GetResult();
            }
            catch (InvalidDataException)
            {
                CaughtMalformedBody = true;
            }
            return body;
        }
    }
}
