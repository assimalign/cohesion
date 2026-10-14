using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Caching.Internal;
using Assimalign.Cohesion.Web.Caching.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Caching.Tests;

[CollectionDefinition("Output cache events", DisableParallelization = true)]
public sealed class OutputCacheEventSourceCollection { }

[Collection("Output cache events")]
public sealed class OutputCacheEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Caching] - EventSource: Name matches assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(OutputCacheEventSource)).ShouldBe("Assimalign.Cohesion.Web.Caching");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - EventSource: Strict manifest succeeds")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only strict manifest verification.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only strict manifest verification.")]
    public void GenerateManifest_Source_ShouldBeStrict()
    {
        // Arrange / Act / Assert
        EventSource.GenerateManifest(typeof(OutputCacheEventSource), null, EventManifestOptions.Strict).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Caching] - Response skip: Reports final reason and request without cache data")]
    [InlineData("BodyOverCap")]
    [InlineData("StatusNotCacheable")]
    [InlineData("SetCookie")]
    [InlineData("CacheControlForbids")]
    [InlineData("VaryStar")]
    [InlineData("NoFreshness")]
    [InlineData("DecidedBeforeRouting")]
    public async Task InvokeAsync_UnstorableResponse_ShouldReportFinalReason(string reason)
    {
        // Arrange
        using InMemoryOutputCacheStore store = new();
        OutputCacheOptions options = new();
        options.AddBasePolicy(policy => policy.Tags.Add("secret-tag"));
        options.MaximumBodySize = reason == "BodyOverCap" ? 1 : 1024;
        OutputCacheMiddleware middleware = new(store, options);
        OutputCacheTestContext context = CreateContext(out string requestId);
        OutputCacheCompletionFeature completion = new();
        context.Features.Set<IWebResponseCompletionFeature>(completion);
        using Activity ambient = new Activity("unrelated").Start();
        using EventSourceRecorder recorder = new(OutputCacheEventSource.Log, EventLevel.Verbose);

        // Act — response restrictions are applied after middleware unwinds, as starting callbacks do.
        await middleware.InvokeAsync(context, async current =>
        {
            if (reason == "DecidedBeforeRouting")
            {
                current.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(OutputCacheMetadata.Disabled));
            }
            await current.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("secret-body"), current.RequestCancelled);
        });
        if (reason != "DecidedBeforeRouting")
        {
            recorder.Events.ShouldNotContain(entry => entry.EventId == 1);
        }
        switch (reason)
        {
            case "StatusNotCacheable": context.Response.StatusCode = HttpStatusCode.InternalServerError; break;
            case "SetCookie": context.Response.Headers[HttpHeaderKey.SetCookie] = "session=secret-cookie"; break;
            case "CacheControlForbids": context.Response.Headers[HttpHeaderKey.CacheControl] = "private"; break;
            case "VaryStar": context.Response.Headers[HttpHeaderKey.Vary] = "*"; break;
            case "NoFreshness": context.Response.Headers[HttpHeaderKey.CacheControl] = "max-age=0"; break;
        }
        await completion.CompleteAsync();

        // Assert
        EventWrittenEventArgs entry = recorder.Events.Single(entry => entry.EventId == 1 && entry.Payload![2]?.Equals(requestId) == true);
        entry.Level.ShouldBe(EventLevel.Verbose);
        entry.PayloadNames!.ToArray().ShouldBe(new[] { "reason", "routeTemplate", "requestId" });
        entry.Payload!.ToArray().ShouldBe(new object[] { reason, "/content/{id}", requestId });
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
        recorder.Events.SelectMany(entry => entry.Payload ?? []).OfType<string>()
            .ShouldNotContain(value => value.Contains("secret", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Decline: Throttles reports while counting every oversized entry")]
    public async Task SetAsync_OversizedEntries_ShouldThrottleReportsAndCountExactly()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        using InMemoryOutputCacheStore store = new(sizeLimit: 1);
        OutputCacheEntry oversized = new(HttpStatusCode.Ok, [], [1, 2, 3], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), ["secret-tag"], []);
        OutputCacheEventSource source = OutputCacheEventSource.Log;
        long before = source.DeclinedEntries;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);

        // Act
        for (int index = 0; index < 3; index++)
        {
            await store.SetAsync("secret-cache-key", oversized, timeout.Token);
        }

        // Assert
        source.DeclinedEntries.ShouldBe(before + 3);
        EventWrittenEventArgs entry = recorder.Events.Single(entry => entry.EventId == 2);
        entry.Level.ShouldBe(EventLevel.Warning);
        entry.PayloadNames!.ToArray().ShouldBe(new[] { "entryBytes", "sizeLimitBytes", "suppressedCount" });
        entry.Payload![0].ShouldBe(oversized.Size);
        entry.Payload[1].ShouldBe(1L);
        recorder.Events.SelectMany(entry => entry.Payload ?? []).OfType<string>().ShouldBeEmpty();
        (await store.GetAsync("secret-cache-key", timeout.Token)).ShouldBeNull();

        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        await store.SetAsync("secret-cache-key", oversized, timeout.Token);
        recorder.Events.Count(entry => entry.EventId == 2).ShouldBe(2);
        recorder.Events.Last().Payload![2].ShouldBe(2);
        source.DeclinedEntries.ShouldBe(before + 4);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Successful response: Does not raise request events")]
    public async Task InvokeAsync_StoredResponse_ShouldNotReportSuccess()
    {
        // Arrange
        using InMemoryOutputCacheStore store = new();
        OutputCacheOptions options = new();
        options.AddBasePolicy(_ => { });
        OutputCacheMiddleware middleware = new(store, options);
        OutputCacheTestContext context = CreateContext(out _);
        OutputCacheCompletionFeature completion = new();
        context.Features.Set<IWebResponseCompletionFeature>(completion);
        using EventSourceRecorder recorder = new(OutputCacheEventSource.Log, EventLevel.Verbose);

        // Act
        await middleware.InvokeAsync(context, _ => Task.CompletedTask);
        await completion.CompleteAsync();

        // Assert
        recorder.Events.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Decline: Counts entries without a listener")]
    public async Task SetAsync_NoListener_ShouldCountEveryDecline()
    {
        // Arrange
        OutputCacheEventSource source = OutputCacheEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        long before = source.DeclinedEntries;
        using InMemoryOutputCacheStore store = new(sizeLimit: 1);
        OutputCacheEntry entry = new(HttpStatusCode.Ok, [], [1], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), [], []);

        // Act
        await store.SetAsync("first", entry, CancellationToken.None);
        await store.SetAsync("second", entry, CancellationToken.None);

        // Assert
        source.DeclinedEntries.ShouldBe(before + 2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - EventSource: Counters publish")]
    public async Task EnableCounters_Source_ShouldPublishEveryCounter()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(OutputCacheEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await recorder.WaitForCountersAsync(["declined-entries"], timeout.Token);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    private static OutputCacheTestContext CreateContext(out string requestId)
    {
        OutputCacheTestContext context = new();
        context.Request.Path = new HttpPath("/secret-path");
        context.Request.QueryCollection["secret-query"] = new HttpQuery("secret-query=secret-value").Parse()["secret-query"];
        RequestIdFeature request = new();
        context.Features.Set<IWebRequestIdFeature>(request);
        context.Features.Set<IWebEndpointFeature>(new EndpointFeature());
        requestId = request.RequestId.ToHexString();
        return context;
    }

    private sealed class RequestIdFeature : IWebRequestIdFeature
    {
        public string Name => nameof(IWebRequestIdFeature);
        public ActivityTraceId RequestId { get; } = ActivityTraceId.CreateRandom();
    }

    private sealed class EndpointFeature : IWebEndpointFeature
    {
        public string Name => nameof(IWebEndpointFeature);
        public WebApplicationMiddleware Endpoint => _ => Task.CompletedTask;
        public string? RouteTemplate => "/content/{id}";
    }
}
