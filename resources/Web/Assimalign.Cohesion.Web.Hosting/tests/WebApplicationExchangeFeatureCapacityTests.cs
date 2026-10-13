using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// The default server sizes each exchange's feature collection (#1381). It counts the application
/// features the pipeline stamps plus the features the host always installs, sets the listener's
/// <c>ExchangeFeatureCapacity</c> to that count before the listener configurations run, and rounds what
/// they leave up to a size an unsized collection grows through. These tests pin the count against a real
/// exchange, so a feature the host starts installing on every exchange without counting it fails here,
/// and they pin the rounding: an exchange carrying more features than counted, such as routing's route
/// match, must never allocate more than an unsized collection would.
/// </summary>
public class WebApplicationExchangeFeatureCapacityTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Feature capacity: The counted features should equal the features a plain exchange carries")]
    [InlineData(WebApplicationTestProtocol.Http1, 0)]
    [InlineData(WebApplicationTestProtocol.Http1, 8)]
    [InlineData(WebApplicationTestProtocol.Http1, 16)]
    [InlineData(WebApplicationTestProtocol.Http2, 0)]
    [InlineData(WebApplicationTestProtocol.Http2, 8)]
    [InlineData(WebApplicationTestProtocol.Http2, 16)]
    public async Task ExchangeFeatureCapacity_PlainRequest_ShouldCountTheFeaturesTheExchangeCarries(
        WebApplicationTestProtocol protocol,
        int applicationFeatureCount)
    {
        // Arrange — a listener configuration runs after the host's defaults and before the rounding, so
        // it observes the count the host chose (and could raise it).
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });

        for (int i = 0; i < applicationFeatureCount; i++)
        {
            ((IWebApplicationBuilder)factory.Builder).AddFeature(new NamedFeature($"Cohesion.Tests.ApplicationFeature{i}"));
        }

        int counted = -1;
        factory.Builder.Server.UseServer(options => counted = options.ExchangeFeatureCapacity);

        int carried = -1;
        factory.Application.Use((context, next) =>
        {
            carried = context.Features.Count();
            return Task.CompletedTask;
        });

        // Act
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/");

        // Assert
        response.IsSuccessStatusCode.ShouldBeTrue();
        counted.ShouldBe(applicationFeatureCount);
        carried.ShouldBe(counted + 5);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Feature capacity: A replacement pipeline stamps no application features, so the count should hold only the host's")]
    public async Task ExchangeFeatureCapacity_WithReplacementPipeline_ShouldCountOnlyTheHostFeatures()
    {
        // Arrange — the features are registered, but a pipeline passed to AddPipeline replaces the one
        // that stamps them.
        await using WebApplicationTestFactory factory = new();

        for (int i = 0; i < 3; i++)
        {
            ((IWebApplicationBuilder)factory.Builder).AddFeature(new NamedFeature($"Cohesion.Tests.ApplicationFeature{i}"));
        }

        FeatureCountingPipeline pipeline = new();
        ((IWebApplicationBuilder)factory.Builder).AddPipeline(pipeline);

        int counted = -1;
        factory.Builder.Server.UseServer(options => counted = options.ExchangeFeatureCapacity);

        // Act
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/");

        // Assert
        response.IsSuccessStatusCode.ShouldBeTrue();
        counted.ShouldBe(0);
        pipeline.Carried.ShouldBe(5);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Feature capacity: Slots a configuration adds should be rounded with the host's count")]
    [InlineData(WebApplicationTestProtocol.Http1)]
    [InlineData(WebApplicationTestProtocol.Http2)]
    public async Task ExchangeFeatureCapacity_WithSlotsAddedInAConfiguration_ShouldRoundAfterTheConfigurations(WebApplicationTestProtocol protocol)
    {
        // Arrange — the server counts its five features; the configuration adds four slots for its
        // middleware, so the count is 9 and rounds to 17. Left at 9, or rounded to 7 before the
        // configuration added its slots, the dictionary would be sized 11 and grow on the twelfth
        // feature. The middleware fills the exchange to 17 features and records what that allocated.
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });
        factory.Builder.Server.UseServer(options => options.ExchangeFeatureCapacity += 4);

        const int rounded = 17;
        IHttpFeature[] middlewareFeatures = CreateFeatures(rounded - 5);

        long allocated = -1;
        int carried = -1;
        factory.Application.Use((context, next) =>
        {
            allocated = Stamp(context.Features, middlewareFeatures);
            carried = context.Features.Count();
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // The first request warms the middleware's code path, so the measured one allocates nothing
        // for first-call work.
        using (HttpResponseMessage warmUp = await client.GetAsync("/"))
        {
            warmUp.IsSuccessStatusCode.ShouldBeTrue();
        }

        // Act
        using HttpResponseMessage response = await client.GetAsync("/");

        // Assert
        response.IsSuccessStatusCode.ShouldBeTrue();
        rounded.ShouldBe(17);
        carried.ShouldBe(rounded);
        allocated.ShouldBe(0L);
    }

    // ------------------------------------------------------------------ helpers

    private static IHttpFeature[] CreateFeatures(int count)
    {
        IHttpFeature[] features = new IHttpFeature[count];

        for (int i = 0; i < count; i++)
        {
            features[i] = new NamedFeature($"Cohesion.Tests.StampedFeature{i}");
        }

        return features;
    }

    /// <summary>
    /// Sets every feature and returns the bytes the current thread allocated doing it.
    /// </summary>
    private static long Stamp(IHttpFeatureCollection collection, IHttpFeature[] features)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        foreach (IHttpFeature feature in features)
        {
            collection.Set(feature);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// Creates a collection sized for <paramref name="capacity"/> (<c>0</c> leaves it unsized), sets every
    /// feature, and returns the bytes the current thread allocated doing both.
    /// </summary>
    private static long CreateAndStamp(int capacity, IHttpFeature[] features)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        HttpFeatureCollection collection = new(capacity);

        foreach (IHttpFeature feature in features)
        {
            collection.Set(feature);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(collection);
        return allocated;
    }

    private sealed class NamedFeature : IHttpFeature
    {
        public NamedFeature(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    private sealed class FeatureCountingPipeline : IWebApplicationPipeline
    {
        public int Carried { get; private set; } = -1;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
        {
            Carried = context.Features.Count();
            return Task.CompletedTask;
        }
    }
}
