using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.RequestTimeouts.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Tests;

[Collection("RequestTimeoutEventSource")]
public sealed class RequestTimeoutEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - GetName: Should match the assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Act / Assert
        EventSource.GetName(typeof(RequestTimeoutEventSource)).ShouldBe("Assimalign.Cohesion.Web.RequestTimeouts");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - GenerateManifest: Should validate strictly")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only strict validation of the statically known event source.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only strict validation of the statically known event source.")]
    public void GenerateManifest_Source_ShouldValidateStrictly()
    {
        // Act / Assert
        EventSource.GenerateManifest(typeof(RequestTimeoutEventSource), null, EventManifestOptions.Strict).ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: Should report expiry and ignored expiry once on the request flow")]
    public async Task InvokeAsync_ExpiredRequests_ShouldReportOnRequestFlow()
    {
        // Arrange
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(15));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), budget.Token);
        RequestTimeoutEventSource source = RequestTimeoutEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        long timedOut = source.TimedOutRequests;
        long ignored = source.IgnoredTimeouts;
        ManualTimeProvider clock = new();
        await using TimeoutTestContext context = CreateContext(out RequestIdentity identity);
        RequestTimeoutMiddleware middleware = CreateMiddleware(clock);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act: fire the timer while downstream is still suspended, then let it unwind.
        Task execution = middleware.InvokeAsync(context, async ctx =>
        {
            ctx.Features.Get<IRequestTimeoutFeature>()!.SetTimeout(TimeSpan.FromSeconds(7));
            await release.Task.WaitAsync(budget.Token);
            ctx.RequestCancelled.ThrowIfCancellationRequested();
        });
        clock.Advance(TimeSpan.FromSeconds(7));
        recorder.Events.ShouldBeEmpty();
        source.TimedOutRequests.ShouldBe(timedOut);
        release.SetResult();
        await execution.WaitAsync(budget.Token);

        await using TimeoutTestContext ignoredContext = CreateContext(out RequestIdentity ignoredIdentity);
        await middleware.InvokeAsync(ignoredContext, ctx =>
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            // Changing a fired timer cannot change the interval that expired.
            ctx.Features.Get<IRequestTimeoutFeature>()!.SetTimeout(TimeSpan.FromSeconds(99));
            return Task.CompletedTask;
        });

        // Assert
        EventWrittenEventArgs expired = ForRequest(recorder, identity).Single();
        expired.EventId.ShouldBe(1);
        expired.Level.ShouldBe(EventLevel.Warning);
        expired.PayloadNames!.ShouldBe(new[] { "timeoutMilliseconds", "responseStarted", "statusCode", "routeTemplate", "suppressedCount", "requestId" });
        expired.Payload![0].ShouldBe(7000d);
        expired.Payload[1].ShouldBe(false);
        expired.Payload[2].ShouldBe(504);
        expired.Payload[3].ShouldBe("/jobs/{id}");
        context.Response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        EventWrittenEventArgs completed = ForRequest(recorder, ignoredIdentity).Single();
        completed.EventId.ShouldBe(2);
        completed.Level.ShouldBe(EventLevel.Verbose);
        completed.PayloadNames!.ShouldBe(new[] { "timeoutMilliseconds", "routeTemplate", "requestId" });
        completed.Payload![0].ShouldBe(30000d);
        source.TimedOutRequests.ShouldBe(timedOut + 1);
        source.IgnoredTimeouts.ShouldBe(ignored + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        context.Features.Get<IRequestTimeoutFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Counters: Should publish exact totals")]
    public async Task OnEventCommand_CountersEnabled_ShouldPublishTotals()
    {
        // Arrange
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(RequestTimeoutEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await recorder.WaitForCountersAsync(new[] { "timed-out-requests", "ignored-timeouts" }, budget.Token);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: Started responses should abort and warnings should throttle without losing counts")]
    public async Task InvokeAsync_StartedResponses_ShouldThrottleWarningsAndCountEveryTimeout()
    {
        // Arrange
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(15));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), budget.Token);
        RequestTimeoutEventSource source = RequestTimeoutEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        long before = source.TimedOutRequests;
        ManualTimeProvider clock = new();
        RequestTimeoutMiddleware middleware = CreateMiddleware(clock);
        await using TimeoutTestContext first = CreateContext(out RequestIdentity firstId);
        first.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(true));
        await using TimeoutTestContext second = CreateContext(out RequestIdentity secondId);
        await using TimeoutTestContext third = CreateContext(out RequestIdentity thirdId);

        // Act
        await ExpireAsync(middleware, clock, first);
        await ExpireAsync(middleware, clock, second);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), budget.Token);
        await ExpireAsync(middleware, clock, third);

        // Assert
        EventWrittenEventArgs aborted = ForRequest(recorder, firstId).Single();
        aborted.Payload![1].ShouldBe(true);
        aborted.Payload[2].ShouldBe(0);
        first.CancelRequested.ShouldBeTrue();
        ForRequest(recorder, secondId).ShouldBeEmpty();
        ForRequest(recorder, thirdId).Single().Payload![4].ShouldBe(1);
        source.TimedOutRequests.ShouldBe(before + 3);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: Rate faults racing expiry should not be reported as timeouts")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvokeAsync_RateFaultRacesTimeout_ShouldLeaveTransportOutcomeAlone(bool responseFault, bool throwCancellation)
    {
        // Arrange
        RequestTimeoutEventSource source = RequestTimeoutEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        long timedOut = source.TimedOutRequests;
        long ignored = source.IgnoredTimeouts;
        ManualTimeProvider clock = new();
        await using TimeoutTestContext context = CreateContext(out RequestIdentity identity);
        RequestTimeoutMiddleware middleware = CreateMiddleware(clock);
        OperationCanceledException cancellation = new();

        // Act
        Task execution = middleware.InvokeAsync(context, _ =>
        {
            if (responseFault)
            {
                context.Features.Set<IWebResponseClientFaultFeature>(new ResponseRateFault());
            }
            else
            {
                context.Features.Set<IWebClientFaultFeature>(new RequestRateFault());
            }

            clock.Advance(TimeSpan.FromSeconds(30));
            return throwCancellation ? Task.FromException(cancellation) : Task.CompletedTask;
        });
        if (throwCancellation)
        {
            await Should.ThrowAsync<OperationCanceledException>(() => execution);
            execution.IsCanceled.ShouldBeTrue();
        }
        else
        {
            await execution;
        }

        // Assert
        ForRequest(recorder, identity).ShouldBeEmpty();
        source.TimedOutRequests.ShouldBe(timedOut);
        source.IgnoredTimeouts.ShouldBe(ignored);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.CancelRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: Disabled events should retain totals without reading identifiers")]
    public async Task InvokeAsync_NoListener_ShouldCountWithoutReadingIdentifiers()
    {
        // Arrange
        RequestTimeoutEventSource source = RequestTimeoutEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        long timedOut = source.TimedOutRequests;
        long ignored = source.IgnoredTimeouts;
        ManualTimeProvider clock = new();
        RequestTimeoutMiddleware middleware = CreateMiddleware(clock);
        await using TimeoutTestContext context = CreateContext(out RequestIdentity identity);

        // Act
        await ExpireAsync(middleware, clock, context);
        await middleware.InvokeAsync(context, _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        });

        // Assert
        source.TimedOutRequests.ShouldBe(timedOut + 1);
        source.IgnoredTimeouts.ShouldBe(ignored + 1);
        identity.Reads.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: Transport cancellation racing expiry should not double report")]
    public async Task InvokeAsync_TransportCancelled_ShouldNotReportTimeout()
    {
        // Arrange
        RequestTimeoutEventSource source = RequestTimeoutEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        long timedOut = source.TimedOutRequests;
        long ignored = source.IgnoredTimeouts;
        ManualTimeProvider clock = new();
        await using TimeoutTestContext context = CreateContext(out RequestIdentity identity);

        // Act
        await CreateMiddleware(clock).InvokeAsync(context, _ =>
        {
            context.AbortClient();
            clock.Advance(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        });

        // Assert
        ForRequest(recorder, identity).ShouldBeEmpty();
        source.TimedOutRequests.ShouldBe(timedOut);
        source.IgnoredTimeouts.ShouldBe(ignored);
    }

    private static RequestTimeoutMiddleware CreateMiddleware(ManualTimeProvider clock)
        => new(new RequestTimeoutOptions
        {
            TimeProvider = clock,
            SuspendWhenDebuggerAttached = false,
            DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(30) }
        });

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - SetTimeout: A queued old callback should preserve the new arm")]
    public async Task SetTimeout_PreviousCallbackQueued_ShouldPreserveNewDeadlineAndInterval()
    {
        // Arrange
        ManualTimeProvider clock = new();
        await using TimeoutTestContext context = new();
        using RequestTimeoutFeature feature = new(context, null, clock);
        feature.SetTimeout(TimeSpan.FromSeconds(7));

        // Act: the old callback is due but has not run when the handler changes the interval.
        clock.Advance(TimeSpan.FromSeconds(7), () => feature.SetTimeout(TimeSpan.FromSeconds(11)));

        // Assert
        feature.TimedOut.ShouldBeFalse();
        feature.Token.IsCancellationRequested.ShouldBeFalse();
        feature.ArmedTimeout.ShouldBe(TimeSpan.FromSeconds(11));
        clock.Advance(TimeSpan.FromSeconds(11));
        feature.TimedOut.ShouldBeTrue();
        feature.Token.IsCancellationRequested.ShouldBeTrue();
        feature.SetTimeout(TimeSpan.FromSeconds(99));
        feature.ArmedTimeout.ShouldBe(TimeSpan.FromSeconds(11));
        Should.Throw<ArgumentOutOfRangeException>(() => feature.SetTimeout(TimeSpan.MaxValue));
    }

    private static Task ExpireAsync(RequestTimeoutMiddleware middleware, ManualTimeProvider clock, TimeoutTestContext context)
        => middleware.InvokeAsync(context, ctx =>
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            return Task.FromCanceled(ctx.RequestCancelled);
        });

    private static TimeoutTestContext CreateContext(out RequestIdentity identity)
    {
        TimeoutTestContext context = new("/private-input");
        identity = new RequestIdentity();
        context.Features.Set<IWebRequestIdFeature>(identity);
        context.Features.Set<IRouterEndpointFeature>(new TestEndpoint());
        return context;
    }

    private static EventWrittenEventArgs[] ForRequest(EventSourceRecorder recorder, RequestIdentity identity)
        => recorder.Events.Where(e => e.PayloadNames?.Contains("requestId") == true &&
            Equals(e.Payload![e.PayloadNames.IndexOf("requestId")], identity.Id.ToHexString())).ToArray();

    private sealed class RequestIdentity : IWebRequestIdFeature
    {
        internal ActivityTraceId Id { get; } = ActivityTraceId.CreateRandom();
        internal int Reads { get; private set; }
        public string Name => nameof(IWebRequestIdFeature);
        public ActivityTraceId RequestId
        {
            get
            {
                Reads++;
                return Id;
            }
        }
    }

    private sealed class TestEndpoint : IRouterEndpointFeature
    {
        public string Name => nameof(IRouterEndpointFeature);
        public WebApplicationMiddleware Endpoint => _ => Task.CompletedTask;
        public string RouteTemplate => "/jobs/{id}";
    }

    private sealed class RequestRateFault : IWebClientFaultFeature
    {
        public string Name => nameof(IWebClientFaultFeature);
        public HttpStatusCode? StatusCode => HttpStatusCode.RequestTimeout;
    }

    private sealed class ResponseRateFault : IWebResponseClientFaultFeature
    {
        public string Name => nameof(IWebResponseClientFaultFeature);
        public bool ResponseAbortedByClient => true;
    }
}
