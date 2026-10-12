using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Sessions.Internal;
using Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

[CollectionDefinition("Web session events", DisableParallelization = true)]
public sealed class WebSessionEventSourceCollection { }

[Collection("Web session events")]
public sealed class WebSessionEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - EventSource: Name matches assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(WebSessionEventSource)).ShouldBe("Assimalign.Cohesion.Web.Sessions");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - EventSource: Strict manifest succeeds")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest verification.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest verification.")]
    public void GenerateManifest_Source_ShouldBeStrict()
    {
        // Arrange / Act / Assert
        EventSource.GenerateManifest(typeof(WebSessionEventSource), null, EventManifestOptions.Strict).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - SessionOrphaned: Late access emits only Verbose without credentials")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_LateCookielessAccess_ShouldReportVerboseOnce(bool modified)
    {
        // Arrange
        await using SessionTestContext context = new();
        RequestIdFeature requestId = new();
        context.Features.Set<IWebRequestIdFeature>(requestId);
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(true));
        RecordingSessionStore store = new();
        SessionMiddleware middleware = new(store, new HttpSessionOptions());
        WebSessionEventSource source = WebSessionEventSource.Log;
        long before = source.OrphanedSessions;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        string? sessionId = null;

        // Act — read-only materialization is sufficient; it does not lose any state.
        await middleware.InvokeAsync(context, exchange =>
        {
            IHttpSession session = exchange.Session!;
            sessionId = session.Id;
            if (modified)
            {
                session.SetString("secret-key", "secret-value");
            }
            return Task.CompletedTask;
        });

        // Assert
        source.OrphanedSessions.ShouldBe(before + 1);
        EventWrittenEventArgs entry = recorder.Events.Single(e =>
            e.EventId == 1 && Equals(e.Payload![1], requestId.RequestId.ToHexString()));
        entry.Level.ShouldBe(EventLevel.Verbose);
        entry.PayloadNames!.ToArray().ShouldBe(new[] { "reason", "requestId" });
        entry.Payload![0].ShouldBe("ResponseStarted");
        recorder.Events.ShouldNotContain(e => e.EventId == 0 || e.Level == EventLevel.Warning);
        recorder.Events.SelectMany(e => e.Payload ?? []).OfType<string>()
            .ShouldNotContain(value => value == sessionId || value.Contains("secret", StringComparison.Ordinal));
        context.Response.Cookies.ShouldBeEmpty();
        store.SetCount.ShouldBe(0);
        store.RefreshCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - EventSource: Disabled events retain exact orphan count without reading request id")]
    public async Task InvokeAsync_ListenerDisabled_ShouldCountWithoutReadingRequestId()
    {
        // Arrange
        await using SessionTestContext context = new();
        context.Features.Set<IWebRequestIdFeature>(new UnreadableRequestIdFeature());
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(true));
        SessionMiddleware middleware = new(new InMemoryHttpSessionStore(), new HttpSessionOptions());
        long before = WebSessionEventSource.Log.OrphanedSessions;

        // Act
        await middleware.InvokeAsync(context, exchange =>
        {
            _ = exchange.Session;
            return Task.CompletedTask;
        });

        // Assert
        WebSessionEventSource.Log.OrphanedSessions.ShouldBe(before + 1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - EventSource: Counters publish")]
    public async Task EnableCounters_Source_ShouldPublishEveryCounter()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(WebSessionEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await recorder.WaitForCountersAsync(["orphaned-sessions"], timeout.Token);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    private sealed class RequestIdFeature : IWebRequestIdFeature
    {
        public string Name => nameof(IWebRequestIdFeature);
        public ActivityTraceId RequestId { get; } = ActivityTraceId.CreateRandom();
    }

    private sealed class UnreadableRequestIdFeature : IWebRequestIdFeature
    {
        public string Name => nameof(IWebRequestIdFeature);
        public ActivityTraceId RequestId => throw new InvalidOperationException("Request id read with no listener.");
    }
}
