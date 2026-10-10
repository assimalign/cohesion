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
/// The default server sizes each exchange's feature collection (#1381): it sets the listener's
/// <c>ExchangeFeatureCapacity</c> to the application features the pipeline stamps plus the features
/// the host always installs, so stamping never grows the collection. These tests pin the count against
/// a real exchange: a plain request must carry exactly as many features as the capacity, so a feature
/// the host starts installing on every exchange without counting it fails here.
/// </summary>
public class WebApplicationExchangeFeatureCapacityTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Feature capacity: The capacity should equal the features a plain exchange carries")]
    [InlineData(WebApplicationTestProtocol.Http1, 0)]
    [InlineData(WebApplicationTestProtocol.Http1, 8)]
    [InlineData(WebApplicationTestProtocol.Http1, 16)]
    [InlineData(WebApplicationTestProtocol.Http2, 0)]
    [InlineData(WebApplicationTestProtocol.Http2, 8)]
    [InlineData(WebApplicationTestProtocol.Http2, 16)]
    public async Task ExchangeFeatureCapacity_PlainRequest_ShouldEqualTheFeaturesTheExchangeCarries(
        WebApplicationTestProtocol protocol,
        int applicationFeatureCount)
    {
        // Arrange — a listener configuration runs after the host's defaults, so it observes the
        // capacity the host chose (and could replace it).
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });

        for (int i = 0; i < applicationFeatureCount; i++)
        {
            ((IWebApplicationBuilder)factory.Builder).AddFeature(new NamedFeature($"Cohesion.Tests.ApplicationFeature{i}"));
        }

        int capacity = -1;
        factory.Builder.Server.UseServer(options => capacity = options.ExchangeFeatureCapacity);

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
        capacity.ShouldBe(WebApplicationServerBuilder.HostFeatureCount + applicationFeatureCount);
        carried.ShouldBe(capacity);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Feature capacity: A replacement pipeline stamps no application features, so the capacity should count only the host's")]
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

        int capacity = -1;
        factory.Builder.Server.UseServer(options => capacity = options.ExchangeFeatureCapacity);

        // Act
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/");

        // Assert
        response.IsSuccessStatusCode.ShouldBeTrue();
        capacity.ShouldBe(WebApplicationServerBuilder.HostFeatureCount);
        pipeline.Carried.ShouldBe(capacity);
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
