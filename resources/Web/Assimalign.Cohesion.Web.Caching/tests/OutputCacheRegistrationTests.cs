using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Caching;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>
/// "Add configures, Use places": <see cref="OutputCacheBuilder"/>, which
/// <c>builder.Services.AddOutputCache(...)</c> builds, copies its options into the middleware and owns
/// only the store it creates; <c>UseOutputCache()</c> places the last registered middleware, or one with
/// the opt-in defaults when the application registered none.
/// </summary>
public class OutputCacheRegistrationTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Caching] - UseOutputCache: Should place the middleware the application registered")]
    public async Task UseOutputCache_WithRegisteredMiddleware_ShouldPlaceIt()
    {
        // Arrange — the registered middleware answers from its store, so the endpoint never runs.
        Endpoint endpoint = new();
        TestPipelineBuilder pipeline = new() { Terminal = endpoint.InvokeAsync };
        pipeline.Registered.Add(BuildOutputCache(cache => cache.UseStore(StoreHolding("registered")).Options.AddBasePolicy(_ => { })));
        OutputCacheTestContext context = new();

        // Act
        IWebApplicationPipelineBuilder returned = pipeline.UseOutputCache();
        await RunAsync(pipeline.LastComposed.ShouldNotBeNull(), context);

        // Assert
        returned.ShouldBeSameAs(pipeline);
        ReadBody(context).ShouldBe("registered");
        endpoint.Invocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - UseOutputCache: Should place the last registration when the cache is registered twice")]
    public async Task UseOutputCache_WithTwoRegistrations_ShouldPlaceTheLast()
    {
        // Arrange
        TestPipelineBuilder pipeline = new();
        pipeline.Registered.Add(BuildOutputCache(cache => cache.UseStore(StoreHolding("first")).Options.AddBasePolicy(_ => { })));
        pipeline.Registered.Add(BuildOutputCache(cache => cache.UseStore(StoreHolding("last")).Options.AddBasePolicy(_ => { })));
        OutputCacheTestContext context = new();

        // Act
        pipeline.UseOutputCache();
        await RunAsync(pipeline.LastComposed.ShouldNotBeNull(), context);

        // Assert
        ReadBody(context).ShouldBe("last");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - UseOutputCache: Should place the opt-in defaults when the application registered none")]
    public async Task UseOutputCache_WithoutRegistration_ShouldPlaceTheOptInDefaults()
    {
        // Arrange — with no base policy, a cacheable response from an endpoint without metadata is not stored.
        Endpoint endpoint = new();
        TestPipelineBuilder pipeline = new() { Terminal = endpoint.InvokeAsync };
        OutputCacheTestContext first = new();
        OutputCacheTestContext second = new();

        // Act
        pipeline.UseOutputCache();
        WebApplicationMiddleware placed = pipeline.LastComposed.ShouldNotBeNull();
        await RunAsync(placed, first);
        await RunAsync(placed, second);

        // Assert — the output-cache middleware ran (it publishes its eviction handle) and served no hit.
        first.Features.Get<IOutputCacheFeature>().ShouldNotBeNull();
        ReadBody(second).ShouldBe("endpoint-2");
        endpoint.Invocations.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - AddOutputCache: Options changed after Build should not reach the built middleware")]
    public async Task Build_OptionsChangedAfterBuild_ShouldNotReachTheMiddleware()
    {
        // Arrange — the middleware reads its options on every request, so Build hands it a copy, policies
        // included. Either change below would stop the second request from being a hit.
        OutputCacheBuilder cache = new();
        cache.Options.AddBasePolicy(policy => policy.Duration = TimeSpan.FromMinutes(10));
        IWebApplicationMiddleware middleware = cache.Build();
        cache.Options.MaximumBodySize = 1;
        cache.Options.BasePolicy.ShouldNotBeNull().Enabled = false;
        Endpoint endpoint = new();
        OutputCacheTestContext hit = new();

        // Act
        await RunAsync(exchange => middleware.InvokeAsync(exchange, endpoint.InvokeAsync), new OutputCacheTestContext());
        await RunAsync(exchange => middleware.InvokeAsync(exchange, endpoint.InvokeAsync), hit);

        // Assert
        ReadBody(hit).ShouldBe("endpoint-1");
        endpoint.Invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - AddOutputCache: UseStore refuses a null store and chains")]
    public void UseStore_NullOrStore_ShouldRefuseNullAndReturnTheBuilder()
    {
        // Arrange
        OutputCacheBuilder cache = new();

        // Act
        OutputCacheBuilder returned = cache.UseStore(new DisposalTrackingOutputCacheStore());

        // Assert
        returned.ShouldBeSameAs(cache);
        Should.Throw<ArgumentNullException>(() => cache.UseStore(null!)).ParamName.ShouldBe("store");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - AddOutputCache: Disposing the middleware should dispose the store it created")]
    public async Task Dispose_WithoutSuppliedStore_ShouldDisposeTheStoreItCreated()
    {
        // Arrange — the container AddOutputCache registers the middleware in disposes it with the application.
        OutputCacheBuilder cache = new();
        cache.Options.AddBasePolicy(_ => { });
        IWebApplicationMiddleware middleware = cache.Build();

        // Act
        ((IDisposable)middleware).Dispose();

        // Assert — the disposed in-memory store refuses the lookup the next request makes.
        await Should.ThrowAsync<CacheException>(
            () => RunAsync(exchange => middleware.InvokeAsync(exchange, new Endpoint().InvokeAsync), new OutputCacheTestContext()));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - AddOutputCache: Disposing the middleware should leave a supplied store to its owner")]
    public void Dispose_WithSuppliedStore_ShouldNotDisposeTheStore()
    {
        // Arrange
        DisposalTrackingOutputCacheStore store = new();
        IWebApplicationMiddleware middleware = BuildOutputCache(cache => cache.UseStore(store));

        // Act
        ((IDisposable)middleware).Dispose();

        // Assert
        store.IsDisposed.ShouldBeFalse();
    }

    private static IWebApplicationMiddleware BuildOutputCache(Action<OutputCacheBuilder> configure)
    {
        OutputCacheBuilder cache = new();
        configure(cache);

        return cache.Build();
    }

    private static FixedOutputCacheStore StoreHolding(string body)
        => new(new OutputCacheEntry(HttpStatusCode.Ok, [], Encoding.UTF8.GetBytes(body), DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1), [], []));

    private static async Task RunAsync(WebApplicationMiddleware pipeline, OutputCacheTestContext context)
    {
        // A miss is stored only after successful response completion, which the server reports.
        OutputCacheCompletionFeature completion = new();
        context.Features.Set<IWebResponseCompletionFeature>(completion);
        await pipeline.Invoke(context);
        await completion.CompleteAsync();
    }

    private static string ReadBody(OutputCacheTestContext context)
    {
        context.Response.Body.Position = 0;
        using StreamReader reader = new(context.Response.Body, Encoding.UTF8, leaveOpen: true);

        return reader.ReadToEnd();
    }

    private sealed class Endpoint
    {
        public int Invocations { get; private set; }

        public async Task InvokeAsync(IHttpContext context)
        {
            Invocations++;
            context.Response.StatusCode = HttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"endpoint-{Invocations}"));
        }
    }
}
