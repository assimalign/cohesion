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
using Assimalign.Cohesion.Hosting.Resources;
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

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: An actual connection-cap wait is attributed and counted once")]
    public async Task AcceptLoop_ConnectionCapReached_ShouldReportAndCountTheWait()
    {
        // Arrange
        WebServerEventSource source = WebServerEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpConnection connection = new(new FakeHttpConnectionContext([new FakeHttpContext()]));
        FakeHttpConnectionListener listener = new(connection);
        WebApplicationServer server = new(new WebApplicationServerOptions
        {
            Pipeline = new FakePipeline(async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            }),
            Listener = listener,
            MaxConcurrentConnections = 1,
        });
        long before = source.ConnectionCapWaits;

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            await entered.Task.WaitAsync(_timeout);
            EventWrittenEventArgs entry = await WaitForEventAsync(recorder, server.ServerId, 13);

            // Assert
            listener.AcceptCount.ShouldBe(1);
            source.ConnectionCapWaits.ShouldBe(before + 1);
            ServerEvents(recorder, server.ServerId, 13).ShouldHaveSingleItem();
            entry.EventName.ShouldBe("ConnectionCapReached");
            entry.Level.ShouldBe(EventLevel.Verbose);
            entry.PayloadNames!.ToArray().ShouldBe(["serverId", "maxConcurrentConnections"]);
            entry.Payload!.ToArray().ShouldBe(new object?[] { server.ServerId, 1 });
            recorder.Events.ShouldNotContain(candidate => candidate.EventId == 0);
        }
        finally
        {
            release.TrySetResult();
            await server.StopAsync(CancellationToken.None).WaitAsync(_timeout);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: A real refused response head carries its request identity without reflected data")]
    public async Task SendAsync_InvalidResponseField_ShouldReportRefusalWithTheRequestId()
    {
        // Arrange — permit a fresh report independently of the previous test's process-wide throttle.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        using EventSourceRecorder recorder = new(WebServerEventSource.Log, EventLevel.Verbose);
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
        long before = WebServerEventSource.Log.RefusedResponseHeads;
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
        WebServerEventSource.Log.RefusedResponseHeads.ShouldBe(before + 1);
        recorder.Events.ShouldNotContain(candidate => candidate.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: Refused response-head reports throttle while the counter remains exact")]
    public async Task ResponseHeadRefused_Burst_ShouldReportSuppressionAndCountEveryOccurrence()
    {
        // Arrange — the wrapper is used directly so network scheduling cannot stretch the burst.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        WebServerEventSource source = WebServerEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        FakeHttpContext context = new();
        TestRequestIdFeature identity = new();
        context.Features.Set<IWebRequestIdFeature>(identity);
        InvalidOperationException failure = new("Invalid reflected-secret-header response field.");
        long before = source.RefusedResponseHeads;
        const long serverId = -1398;

        // Act
        source.ResponseHeadRefused(serverId, context, failure);
        source.ResponseHeadRefused(serverId, context, failure);
        source.ResponseHeadRefused(serverId, context, failure);
        EventWrittenEventArgs[] burst = ServerEvents(recorder, serverId, 10);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        source.ResponseHeadRefused(serverId, context, failure);

        // Assert
        burst.ShouldHaveSingleItem();
        EventWrittenEventArgs[] reported = ServerEvents(recorder, serverId, 10);
        reported.Length.ShouldBe(2);
        Payload(reported[1], "suppressedCount").ShouldBe(2);
        reported.ShouldAllBe(entry => Equals(Payload(entry, "requestId"), identity.RequestId.ToHexString()));
        reported.ShouldAllBe(entry => !string.Join(" ", entry.Payload!).Contains("reflected-secret-header", StringComparison.Ordinal));
        source.RefusedResponseHeads.ShouldBe(before + 4);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: Escaping exchange failures are reported while the tracker still drains")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenDrainedAsync_ExchangeIsolationEscapes_ShouldReportItsCause(bool asynchronous)
    {
        // Arrange
        using EventSourceRecorder recorder = new(WebServerEventSource.Log, EventLevel.Verbose);
        MultiplexedExchangeTracker tracker = new();
        InvalidOperationException failure = new($"isolation failure {Guid.NewGuid():N}");

        // Act
        tracker.Start(asynchronous
            ? async () => { await Task.Yield(); throw failure; }
            : () => throw failure);
        await tracker.WhenDrainedAsync().WaitAsync(_timeout);

        // Assert
        EventWrittenEventArgs entry = recorder.Events.Where(candidate => candidate.EventId == 12 &&
            Equals(Payload(candidate, "exceptionMessage"), failure.Message)).ShouldHaveSingleItem();
        entry.EventName.ShouldBe("ExchangeIsolationEscaped");
        entry.Level.ShouldBe(EventLevel.Error);
        entry.PayloadNames!.ToArray().ShouldBe(["exceptionType", "exceptionMessage"]);
        entry.Payload!.ToArray().ShouldBe(new object?[] { failure.GetType().FullName, failure.Message });
        recorder.Events.ShouldNotContain(candidate => candidate.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: An ActivityListener failure is reported without failing the exchange")]
    public async Task ServeExchange_ActivitySamplerThrows_ShouldReportListenerFailureAndContinue()
    {
        // Arrange
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        using EventSourceRecorder recorder = new(WebServerEventSource.Log, EventLevel.Verbose);
        InvalidOperationException failure = new($"sampler failure {Guid.NewGuid():N}");
        using ActivityListener activities = new()
        {
            ShouldListenTo = source => source.Name == "Assimalign.Cohesion.Web.Hosting",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => throw failure,
        };
        ActivitySource.AddActivityListener(activities);
        FakeHttpContext exchange = new(response: new FakeHttpResponse(), request: new FakeHttpRequest("/items", ""));
        FakeHttpConnection connection = new(new FakeHttpConnectionContext([exchange]));
        FakePipeline pipeline = new();
        WebApplicationServer server = new(new WebApplicationServerOptions
        {
            Pipeline = pipeline,
            Listener = new FakeHttpConnectionListener(connection),
        });

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            await connection.Disposed.Task.WaitAsync(_timeout);

            // Assert
            EventWrittenEventArgs entry = recorder.Events.Where(candidate => candidate.EventId == 15 &&
                Equals(Payload(candidate, "exceptionMessage"), failure.Message)).ShouldHaveSingleItem();
            entry.EventName.ShouldBe("TelemetryListenerFaulted");
            entry.Level.ShouldBe(EventLevel.Warning);
            entry.PayloadNames!.ToArray().ShouldBe(["exceptionType", "exceptionMessage", "suppressedCount"]);
            Payload(entry, "exceptionType").ShouldBe(failure.GetType().FullName);
            pipeline.Executed.ShouldHaveSingleItem().ShouldBeSameAs(exchange);
            connection.Context.SendCount.ShouldBe(1);
            connection.AbortCount.ShouldBe(0);
            recorder.Events.ShouldNotContain(candidate => candidate.EventId == 0);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None).WaitAsync(_timeout);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: Telemetry listener failures throttle and the next report carries the suppressed count")]
    public async Task TelemetryListenerFaulted_Burst_ShouldReportSuppressionAfterTheThrottleWindow()
    {
        // Arrange — the actual sampler test covers the raising site; direct calls bound this burst.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        WebServerEventSource source = WebServerEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        InvalidOperationException failure = new($"throttled sampler failure {Guid.NewGuid():N}");

        // Act
        source.TelemetryListenerFaulted(failure);
        source.TelemetryListenerFaulted(failure);
        source.TelemetryListenerFaulted(failure);
        EventWrittenEventArgs[] burst = recorder.Events.Where(entry => entry.EventId == 15 &&
            Equals(Payload(entry, "exceptionMessage"), failure.Message)).ToArray();
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        source.TelemetryListenerFaulted(failure);

        // Assert
        burst.ShouldHaveSingleItem();
        EventWrittenEventArgs[] reported = recorder.Events.Where(entry => entry.EventId == 15 &&
            Equals(Payload(entry, "exceptionMessage"), failure.Message)).ToArray();
        reported.Length.ShouldBe(2);
        reported.ShouldAllBe(entry => entry.EventName == "TelemetryListenerFaulted" && entry.Level == EventLevel.Warning);
        reported[1].PayloadNames!.ToArray().ShouldBe(["exceptionType", "exceptionMessage", "suppressedCount"]);
        reported[1].Payload!.ToArray().ShouldBe(new object?[] { failure.GetType().FullName, failure.Message, 2 });
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: A throwing drain callback preserves draining and stopped lifecycle events")]
    public async Task StopAsync_DrainCallbackThrows_ShouldReportDrainingBeforeStopped()
    {
        // Arrange — application middleware registers on the actual drain feature.
        using EventSourceRecorder recorder = new(WebServerEventSource.Log, EventLevel.Verbose);
        InvalidOperationException failure = new("application drain callback failed");
        CancellationTokenRegistration callback = default;
        FakeHttpConnection connection = new(new FakeHttpConnectionContext([new FakeHttpContext()]));
        FakeHttpConnectionListener listener = new(connection);
        WebApplicationServer server = new(new WebApplicationServerOptions
        {
            Pipeline = new FakePipeline((context, _) =>
            {
                callback = context.Features.Get<IWebServerDrainFeature>()!.Draining.Register(() => throw failure);
                return Task.CompletedTask;
            }),
            Listener = listener,
        });

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            await connection.Disposed.Task.WaitAsync(_timeout);
            AggregateException reportedFailure = await Should.ThrowAsync<AggregateException>(
                () => server.StopAsync(CancellationToken.None).WaitAsync(_timeout));

            // Assert
            reportedFailure.InnerExceptions.ShouldHaveSingleItem().ShouldBeSameAs(failure);
            EventWrittenEventArgs draining = ServerEvents(recorder, server.ServerId, 6).ShouldHaveSingleItem();
            EventWrittenEventArgs stopped = ServerEvents(recorder, server.ServerId, 9).ShouldHaveSingleItem();
            EventWrittenEventArgs[] lifecycle = recorder.Events.Where(entry =>
                (entry.EventId is 6 or 9) && Equals(Payload(entry, "serverId"), server.ServerId)).ToArray();
            lifecycle.Select(entry => entry.EventName).ShouldBe(["ServerDraining", "ServerStopped"]);
            draining.Level.ShouldBe(EventLevel.Informational);
            stopped.Level.ShouldBe(EventLevel.Informational);
            Payload(stopped, "aborted").ShouldBe(false);
            listener.DisposeCount.ShouldBe(1);
            recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
        }
        finally
        {
            callback.Dispose();
            try
            {
                await server.StopAsync(CancellationToken.None).WaitAsync(_timeout);
            }
            catch (AggregateException exception) when (exception.InnerExceptions.Contains(failure))
            {
                // Repeated stops retain the first stop's callback failure after releasing resources.
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: Abort grace expiry reports the remaining exchange and aborted stop")]
    public async Task StopAsync_PipelineIgnoresCancellation_ShouldReportAbortGraceExpiry()
    {
        // Arrange
        using EventSourceRecorder recorder = new(WebServerEventSource.Log, EventLevel.Verbose);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpConnection connection = new(new FakeHttpConnectionContext([new FakeHttpContext()]));
        FakeHttpConnectionListener listener = new(connection);
        WebApplicationServer server = new(new WebApplicationServerOptions
        {
            Pipeline = new FakePipeline(async (_, _) => { entered.TrySetResult(); await release.Task; }),
            Listener = listener,
        });
        using CancellationTokenSource budget = new();
        using CancellationTokenSource timeout = new(_timeout);

        // Act
        try
        {
            await server.StartAsync(CancellationToken.None);
            await entered.Task.WaitAsync(_timeout);
            // The pipeline can enter before its connection task is tracked. The next accept
            // proves registration has completed before the aborted drain snapshots that task.
            while (listener.AcceptCount != 2)
            {
                await Task.Delay(10, timeout.Token);
            }

            Task stop = server.StopAsync(budget.Token);
            budget.Cancel();
            await stop.WaitAsync(_timeout);

            // Assert
            EventWrittenEventArgs entry = ServerEvents(recorder, server.ServerId, 8).ShouldHaveSingleItem();
            entry.EventName.ShouldBe("AbortGraceExpired");
            entry.Level.ShouldBe(EventLevel.Warning);
            entry.PayloadNames!.ToArray().ShouldBe(["serverId", "exchanges", "gracePeriodMilliseconds"]);
            Payload(entry, "exchanges").ShouldBe(1);
            Payload(entry, "gracePeriodMilliseconds").ShouldBe(1000d);
            Payload(ServerEvents(recorder, server.ServerId, 9).ShouldHaveSingleItem(), "aborted").ShouldBe(true);
            recorder.Events.ShouldNotContain(candidate => candidate.EventId == 0);
        }
        finally
        {
            release.TrySetResult();
            budget.Cancel();
            await server.StopAsync(budget.Token).WaitAsync(_timeout);
            await connection.Disposed.Task.WaitAsync(_timeout);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: A resource without an HTTP control endpoint reports disabled control plane once")]
    public async Task Build_ResourceWithoutHttpEndpoint_ShouldReportDisabledControlPlaneOnce()
    {
        // Arrange
        using EventSourceRecorder recorder = new(WebServerEventSource.Log, EventLevel.Verbose);
        string resourceName = $"disabled-control-plane-{Guid.NewGuid():N}";
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            resourceName: resourceName,
            endpoints: new Dictionary<string, Uri> { ["metrics"] = Uri.CreateEndpoint("http", "127.0.0.1", 48123) }));
        WebApplicationBuilder builder = WebApplication.CreateBuilder([], typeof(WebServerEventSourceAcceptanceTests).Assembly);
        await using WebApplication application = builder.Build();

        // Act — the singleton pipeline factory diagnoses the composition when materialized.
        IWebApplicationPipeline pipeline = application.Context.ServiceProvider.GetRequiredService<IWebApplicationPipeline>();
        application.Context.ServiceProvider.GetRequiredService<IWebApplicationPipeline>().ShouldBeSameAs(pipeline);

        // Assert
        EventWrittenEventArgs entry = recorder.Events.Where(candidate => candidate.EventId == 14 &&
            Equals(Payload(candidate, "resourceName"), resourceName)).ShouldHaveSingleItem();
        entry.EventName.ShouldBe("ControlPlaneDisabled");
        entry.Level.ShouldBe(EventLevel.Warning);
        entry.PayloadNames!.ToArray().ShouldBe(["resourceName", "observedEndpoints"]);
        entry.Payload!.ToArray().ShouldBe(new object?[] { resourceName, "metrics" });
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
