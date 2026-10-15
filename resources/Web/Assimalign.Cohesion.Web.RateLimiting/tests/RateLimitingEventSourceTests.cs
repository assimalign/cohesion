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
using Assimalign.Cohesion.Web.RateLimiting.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RateLimiting.Tests;

[Collection(RateLimitingEventSourceCollection.Name)]
public sealed class RateLimitingEventSourceTests
{
    private const string sensitiveValue = "private-partition-and-request-value";

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - EventSource: Provider name matches the assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(RateLimitingEventSource)).ShouldBe("Assimalign.Cohesion.Web.RateLimiting");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - EventSource: Strict manifest is valid")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest validation of the statically referenced event source.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest validation intentionally inspects event methods.")]
    public void GenerateManifest_Source_ShouldBeValid()
    {
        // Arrange / Act
        string? manifest = EventSource.GenerateManifest(typeof(RateLimitingEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNullOrEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: Rejection reports the gate and request id without the partition key")]
    [InlineData("global", "")]
    [InlineData("endpoint", "configured-policy")]
    [InlineData("endpoint", "")]
    public async Task InvokeAsync_ExhaustedPolicy_ShouldReportSafeRejection(string gate, string policyName)
    {
        // Arrange
        RateLimitingPolicy policy = TestPolicies.FixedWindowSingle(sensitiveValue);
        await using var limiter = policy.Limiter;
        RateLimitingMiddleware middleware = CreateMiddleware(policy, gate, policyName);
        await using RateLimitTestContext admitted = CreateContext(policy, gate, policyName);
        await using RateLimitTestContext rejected = CreateContext(policy, gate, policyName);
        string requestId = rejected.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
        using Activity unrelated = new Activity("unrelated").SetIdFormat(ActivityIdFormat.W3C).Start();
        using EventSourceRecorder recorder = new(RateLimitingEventSource.Log, EventLevel.Verbose);
        long before = RateLimitingEventSource.Log.RejectedRequests;

        // Act
        await middleware.InvokeAsync(admitted, _ => Task.CompletedTask);
        await middleware.InvokeAsync(rejected, _ => throw new InvalidOperationException("Rejected request ran downstream."));

        // Assert
        RateLimitingEventSource.Log.RejectedRequests.ShouldBe(before + 1);
        rejected.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        EventWrittenEventArgs rejection = recorder.Events.Single(e => e.EventId == 1 && e.Payload![4]?.ToString() == requestId);
        rejection.Level.ShouldBe(EventLevel.Verbose);
        rejection.PayloadNames!.ShouldBe(new[] { "gate", "policyName", "retryAfterSeconds", "routeTemplate", "requestId" });
        rejection.Payload![0].ShouldBe(gate);
        rejection.Payload[1].ShouldBe(policyName);
        ((double)rejection.Payload[2]!).ShouldBeGreaterThan(0);
        rejection.Payload[3].ShouldBe("/orders/{id}");
        rejection.Payload[4].ShouldNotBe(unrelated.TraceId.ToHexString());
        recorder.Events.Count(e => e.EventId == 1).ShouldBe(1);
        AssertSafeEvents(recorder);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: Concurrency rejection reports absent retry-after")]
    public async Task InvokeAsync_NoRetryAfter_ShouldReportSentinel()
    {
        // Arrange
        RateLimitingPolicy policy = TestPolicies.ConcurrencySingle(sensitiveValue);
        await using var limiter = policy.Limiter;
        await using RateLimitTestContext context = CreateContext(policy);
        using var heldLease = await limiter.AcquireAsync(context, cancellationToken: CancellationToken.None);
        RateLimitingMiddleware middleware = CreateMiddleware(policy);
        using EventSourceRecorder recorder = new(RateLimitingEventSource.Log, EventLevel.Verbose);
        string requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
        long before = RateLimitingEventSource.Log.RejectedRequests;

        // Act
        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        // Assert
        RateLimitingEventSource.Log.RejectedRequests.ShouldBe(before + 1);
        recorder.Events.Single(e => e.EventId == 1 && e.Payload![4]?.ToString() == requestId).Payload![2].ShouldBe(-1d);
        AssertSafeEvents(recorder);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: Committed-head warnings are throttled while rejection counts remain exact")]
    public async Task InvokeAsync_CommittedHeads_ShouldThrottleAbortsAndCountEveryRejection()
    {
        // Arrange
        RateLimitingPolicy policy = TestPolicies.FixedWindowSingle(sensitiveValue);
        await using var limiter = policy.Limiter;
        RateLimitingMiddleware middleware = CreateMiddleware(policy);
        await using RateLimitTestContext admitted = CreateContext(policy);
        await middleware.InvokeAsync(admitted, _ => Task.CompletedTask);
        await using RateLimitTestContext first = CreateContext(policy, committed: true);
        await using RateLimitTestContext second = CreateContext(policy, committed: true);
        await using RateLimitTestContext third = CreateContext(policy, committed: true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        using EventSourceRecorder recorder = new(RateLimitingEventSource.Log, EventLevel.Verbose);
        long before = RateLimitingEventSource.Log.RejectedRequests;
        string firstId = first.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
        string secondId = second.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
        string thirdId = third.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();

        // Act
        await middleware.InvokeAsync(first, _ => Task.CompletedTask);
        await middleware.InvokeAsync(second, _ => Task.CompletedTask);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        await middleware.InvokeAsync(third, _ => Task.CompletedTask);

        // Assert
        RateLimitingEventSource.Log.RejectedRequests.ShouldBe(before + 3);
        first.CancelRequested.ShouldBeTrue();
        second.CancelRequested.ShouldBeTrue();
        third.CancelRequested.ShouldBeTrue();
        EventWrittenEventArgs initial = recorder.Events.Single(e => e.EventId == 2 && e.Payload![2]?.ToString() == firstId);
        initial.Level.ShouldBe(EventLevel.Warning);
        initial.PayloadNames!.ShouldBe(new[] { "gate", "policyName", "requestId", "suppressedCount" });
        initial.Payload![0].ShouldBe("global");
        initial.Payload[1].ShouldBe(string.Empty);
        recorder.Events.ShouldNotContain(e => e.EventId == 2 && (string?)e.Payload![2] == secondId);
        recorder.Events.Single(e => e.EventId == 2 && e.Payload![2]?.ToString() == thirdId).Payload![3].ShouldBe(1);
        foreach (string requestId in new[] { firstId, secondId, thirdId })
        {
            recorder.Events.Count(e => e.EventId == 1 && e.Payload![4]?.ToString() == requestId).ShouldBe(1);
        }
        AssertSafeEvents(recorder);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: Disabled diagnostics maintain counters without reading correlation features")]
    public async Task InvokeAsync_NoListener_ShouldCountWithoutReadingDiagnosticFeatures()
    {
        // Arrange
        RateLimitingEventSource.Log.IsEnabled().ShouldBeFalse();
        RateLimitingPolicy policy = TestPolicies.FixedWindowSingle(sensitiveValue);
        await using var limiter = policy.Limiter;
        RateLimitingMiddleware middleware = CreateMiddleware(policy);
        await using RateLimitTestContext admitted = CreateContext(policy, forbidDiagnosticRead: true);
        await using RateLimitTestContext rejected = CreateContext(policy, forbidDiagnosticRead: true);
        long before = RateLimitingEventSource.Log.RejectedRequests;

        // Act
        await middleware.InvokeAsync(admitted, _ => Task.CompletedTask);
        long afterAdmission = RateLimitingEventSource.Log.RejectedRequests;
        await middleware.InvokeAsync(rejected, _ => Task.CompletedTask);

        // Assert
        afterAdmission.ShouldBe(before);
        RateLimitingEventSource.Log.RejectedRequests.ShouldBe(before + 1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - EventSource: Rejection counter publishes after a listener attaches")]
    public async Task OnEventCommand_Enabled_ShouldPublishRejectionCounter()
    {
        // Arrange
        using EventSourceRecorder recorder = new(RateLimitingEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        // Act
        await recorder.WaitForCountersAsync(new[] { "rejected-requests" }, timeout.Token);

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A failing decision hook cannot hide a rejected lease")]
    public async Task InvokeAsync_DecisionHookThrows_ShouldStillCountRejection()
    {
        // Arrange
        RateLimitingPolicy policy = TestPolicies.FixedWindowSingle(sensitiveValue);
        await using var limiter = policy.Limiter;
        RateLimitingMiddleware middleware = new(new RateLimitingOptions
        {
            GlobalPolicy = policy,
            OnDecision = decision =>
            {
                if (!decision.IsAcquired)
                {
                    throw new InvalidOperationException("Application observation hook failed.");
                }
            },
        });
        await using RateLimitTestContext admitted = CreateContext(policy);
        await using RateLimitTestContext rejected = CreateContext(policy);
        await middleware.InvokeAsync(admitted, _ => Task.CompletedTask);
        long before = RateLimitingEventSource.Log.RejectedRequests;

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(rejected, _ => Task.CompletedTask));

        // Assert
        RateLimitingEventSource.Log.RejectedRequests.ShouldBe(before + 1);
    }

    private static RateLimitingMiddleware CreateMiddleware(RateLimitingPolicy policy, string gate = "global", string policyName = "")
    {
        RateLimitingOptions options = new();
        if (gate == "global")
        {
            options.GlobalPolicy = policy;
        }
        else if (policyName.Length > 0)
        {
            options.AddPolicy(policyName, policy);
        }
        return new RateLimitingMiddleware(options);
    }

    private static RateLimitTestContext CreateContext(
        RateLimitingPolicy policy,
        string gate = "global",
        string policyName = "",
        bool committed = false,
        bool forbidDiagnosticRead = false)
    {
        RateLimitTestContext context = new(path: "/" + sensitiveValue);
        context.Request.Headers[HttpHeaderKey.Authorization] = sensitiveValue;
        context.Features.Set<IWebRequestIdFeature>(new DiagnosticRequestIdFeature(forbidDiagnosticRead));
        context.Features.Set<IRouterEndpointFeature>(new DiagnosticEndpointFeature(forbidDiagnosticRead));
        if (gate == "endpoint")
        {
            context.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(
                policyName.Length == 0 ? new RateLimitingMetadata(policy) : new RateLimitingMetadata(policyName)));
        }
        if (committed)
        {
            context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(true));
        }
        return context;
    }

    private static void AssertSafeEvents(EventSourceRecorder recorder)
    {
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        recorder.Events.SelectMany(e => e.Payload!).OfType<string>().ShouldNotContain(value => value.Contains(sensitiveValue, StringComparison.Ordinal));
    }

    private sealed class DiagnosticRequestIdFeature : IWebRequestIdFeature
    {
        private readonly ActivityTraceId _requestId = ActivityTraceId.CreateRandom();
        private readonly bool _forbidRead;

        public DiagnosticRequestIdFeature(bool forbidRead) => _forbidRead = forbidRead;

        public string Name => nameof(IWebRequestIdFeature);
        public ActivityTraceId RequestId => _forbidRead ? throw new InvalidOperationException("Disabled diagnostics read request id.") : _requestId;
    }

    private sealed class DiagnosticEndpointFeature : IRouterEndpointFeature
    {
        private readonly bool _forbidRead;

        public DiagnosticEndpointFeature(bool forbidRead) => _forbidRead = forbidRead;

        public string Name => nameof(IRouterEndpointFeature);
        public WebApplicationMiddleware Endpoint => _ => Task.CompletedTask;
        public string? RouteTemplate => _forbidRead ? throw new InvalidOperationException("Disabled diagnostics read route template.") : "/orders/{id}";
    }
}
