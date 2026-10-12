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
using Assimalign.Cohesion.Web.Sessions.Internal;
using Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

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

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - SessionOrphaned: Late access warns only for modified state without credentials")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_LateCookielessAccess_ShouldWarnOnlyWhenModified(bool modified)
    {
        // Arrange — allow the process-wide warning throttle from earlier tests to expire.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        await using SessionTestContext context = new();
        RequestIdFeature requestId = new();
        context.Features.Set<IWebRequestIdFeature>(requestId);
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(true));
        RecordingSessionStore store = new();
        SessionMiddleware middleware = new(store, new HttpSessionOptions());
        WebSessionEventSource source = WebSessionEventSource.Log;
        long before = source.OrphanedSessions;
        long lossesBefore = source.LostSessions;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        string? sessionId = null;

        // Act
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
        source.LostSessions.ShouldBe(lossesBefore + (modified ? 1 : 0));
        EventWrittenEventArgs entry = recorder.Events.Single(e =>
            e.EventId == 1 && Equals(e.Payload![1], requestId.RequestId.ToHexString()));
        entry.Level.ShouldBe(EventLevel.Verbose);
        entry.PayloadNames!.ToArray().ShouldBe(new[] { "reason", "requestId" });
        entry.Payload![0].ShouldBe("ResponseStarted");
        if (modified)
        {
            EventWrittenEventArgs warning = recorder.Events.Single(e =>
                e.EventId == 2 && Equals(e.Payload![1], requestId.RequestId.ToHexString()));
            warning.EventName.ShouldBe("SessionStateLost");
            warning.Level.ShouldBe(EventLevel.Warning);
            warning.PayloadNames!.ToArray().ShouldBe(new[] { "reason", "requestId", "suppressedCount" });
            warning.Payload![0].ShouldBe("ResponseStarted");
        }
        else
        {
            recorder.Events.ShouldNotContain(e => e.Level == EventLevel.Warning);
        }
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        recorder.Events.SelectMany(e => e.Payload ?? []).OfType<string>()
            .ShouldNotContain(value => value == sessionId || value.Contains("secret", StringComparison.Ordinal));
        context.Response.Cookies.ShouldBeEmpty();
        store.SetCount.ShouldBe(0);
        store.RefreshCount.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - EventSource: Disabled events retain exact orphan and loss counts without reading request id")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_ListenerDisabled_ShouldCountWithoutReadingRequestId(bool modified)
    {
        // Arrange
        await using SessionTestContext context = new();
        context.Features.Set<IWebRequestIdFeature>(new UnreadableRequestIdFeature());
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(true));
        SessionMiddleware middleware = new(new InMemoryHttpSessionStore(), new HttpSessionOptions());
        long before = WebSessionEventSource.Log.OrphanedSessions;
        long lossesBefore = WebSessionEventSource.Log.LostSessions;

        // Act
        await middleware.InvokeAsync(context, exchange =>
        {
            IHttpSession session = exchange.Session!;
            if (modified)
            {
                session.SetString("key", "value");
            }
            return Task.CompletedTask;
        });

        // Assert
        WebSessionEventSource.Log.OrphanedSessions.ShouldBe(before + 1);
        WebSessionEventSource.Log.LostSessions.ShouldBe(lossesBefore + (modified ? 1 : 0));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - SessionStateLost: Warning listener receives throttled losses with exact counts")]
    public async Task InvokeAsync_RepeatedModifiedOrphans_ShouldThrottleWarningsAndCountEveryLoss()
    {
        // Arrange — expire any earlier process-wide throttle before starting this burst.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        WebSessionEventSource source = WebSessionEventSource.Log;
        long orphansBefore = source.OrphanedSessions;
        long lossesBefore = source.LostSessions;
        RecordingSessionStore store = new();
        SessionMiddleware middleware = new(store, new HttpSessionOptions());
        using EventSourceRecorder recorder = new(source, EventLevel.Warning);
        RequestIdFeature requestId = new();

        // Act — each invocation represents a separate cookieless request after its head started.
        for (int request = 0; request < 3; request++)
        {
            await using SessionTestContext context = new();
            context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(true));
            context.Features.Set<IWebRequestIdFeature>(request == 0 ? requestId : new UnreadableRequestIdFeature());
            await middleware.InvokeAsync(context, exchange =>
            {
                exchange.Session!.SetString("secret-key", "secret-value");
                return Task.CompletedTask;
            });
        }

        // Assert — Warning-only monitoring sees the loss, while suppressed requests do no formatting.
        source.OrphanedSessions.ShouldBe(orphansBefore + 3);
        source.LostSessions.ShouldBe(lossesBefore + 3);
        EventWrittenEventArgs first = recorder.Events.Single(e => e.EventId == 2);
        first.Level.ShouldBe(EventLevel.Warning);
        first.Payload![1].ShouldBe(requestId.RequestId.ToHexString());
        recorder.Events.ShouldNotContain(e => e.EventId == 0 || e.EventId == 1);

        // Act — the next emitted loss accounts for both suppressed requests.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        await using SessionTestContext nextContext = new();
        nextContext.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(true));
        nextContext.Features.Set<IWebRequestIdFeature>(requestId);
        await middleware.InvokeAsync(nextContext, exchange =>
        {
            exchange.Session!.SetString("secret-key", "secret-value");
            return Task.CompletedTask;
        });

        // Assert
        source.OrphanedSessions.ShouldBe(orphansBefore + 4);
        source.LostSessions.ShouldBe(lossesBefore + 4);
        recorder.Events.Count(e => e.EventId == 2).ShouldBe(2);
        recorder.Events.Last(e => e.EventId == 2).Payload![2].ShouldBe(2);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        store.SetCount.ShouldBe(0);
        store.RefreshCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - EventSource: Counters publish")]
    public async Task EnableCounters_Source_ShouldPublishEveryCounter()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(WebSessionEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await recorder.WaitForCountersAsync(["orphaned-sessions", "lost-sessions"], timeout.Token);
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
