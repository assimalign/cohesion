using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

[Collection(nameof(TelemetryCollection))]
public sealed class WebServerEventSourceAcceptanceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: A real refused response head carries its request identity without reflected data")]
    public async Task SendAsync_InvalidResponseField_ShouldReportRefusalWithTheRequestId()
    {
        // Arrange — permit a fresh report independently of the previous test's process-wide throttle.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        using EventSourceRecorder recorder = new(await ServerDiagnosticsSource.GetAsync(), EventLevel.Verbose,
            TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource timeout = new(_timeout);
        await using WebApplicationTestFactory factory = new();
        string? requestId = null;
        factory.Application.Use((context, _) =>
        {
            requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
            context.Response.Headers[HttpHeaderKey.Location] = "/private\r\nX-Secret: reflected-secret";
            return Task.CompletedTask;
        });
        using HttpClient client = factory.CreateClient();
        long serverId = (long)Payload(recorder.Events.Single(entry => entry.EventId == 1), "serverId")!;
        EventSourceRecorder.CounterSample before = await recorder.WaitForCounterAsync("refused-response-heads", 0, timeout.Token);
        using Activity unrelated = new Activity("unrelated-caller").Start();
        ActivityTraceId incomingTrace = ActivityTraceId.CreateRandom();
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/private?token=secret");
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{incomingTrace.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.InternalServerError);
        EventWrittenEventArgs entry = ServerEvents(recorder, serverId, 10).ShouldHaveSingleItem();
        entry.EventName.ShouldBe("ResponseHeadRefused");
        entry.Level.ShouldBe(EventLevel.Error);
        entry.PayloadNames!.ToArray().ShouldBe(["serverId", "requestId", "protocolVersion", "routeTemplate", "exceptionMessage", "suppressedCount"]);
        Payload(entry, "requestId").ShouldBe(requestId);
        requestId.ShouldNotBeNullOrEmpty();
        requestId.ShouldBe(incomingTrace.ToHexString());
        requestId.ShouldNotBe(unrelated.TraceId.ToHexString());
        Payload(entry, "protocolVersion").ShouldBe("1.1");
        string text = string.Join(" ", entry.Payload!);
        text.ShouldNotContain("reflected-secret", Case.Sensitive);
        text.ShouldNotContain("/private", Case.Sensitive);
        text.ShouldNotContain("token=secret", Case.Sensitive);
        EventSourceRecorder.CounterSample after = await recorder.WaitForCounterAsync(
            "refused-response-heads", recorder.CounterSequence, timeout.Token);
        after.Value.ShouldBe(before.Value + 1);
        recorder.Events.ShouldNotContain(candidate => candidate.EventId == 0);
    }

    private static EventWrittenEventArgs[] ServerEvents(EventSourceRecorder recorder, long serverId, int eventId)
        => recorder.Events.Where(entry => entry.EventId == eventId &&
            entry.PayloadNames is { Count: > 0 } && entry.PayloadNames[0] == "serverId" &&
            Equals(entry.Payload![0], serverId)).ToArray();

    private static object? Payload(EventWrittenEventArgs entry, string name)
        => entry.Payload![entry.PayloadNames!.IndexOf(name)];

    private static async Task<EventWrittenEventArgs> WaitForEventAsync(EventSourceRecorder recorder, long serverId, int eventId)
    {
        using CancellationTokenSource timeout = new(_timeout);
        while (true)
        {
            EventWrittenEventArgs? entry = ServerEvents(recorder, serverId, eventId).FirstOrDefault();
            if (entry is not null)
            {
                return entry;
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class TestRequestIdFeature : IWebRequestIdFeature
    {
        public string Name => nameof(IWebRequestIdFeature);
        public ActivityTraceId RequestId { get; } = ActivityTraceId.CreateRandom();
    }
}
