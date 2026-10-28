using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

/// <summary>
/// A Web resource instance against a throwaway gateway: the instance publishes its manifest, the
/// gateway reads and writes its CORS policy through the control plane, and a new instance starts
/// with the policy the gateway kept.
/// </summary>
public sealed class WebResourceEndToEndTests
{
    private const string DisplayPrefix = "Cohesion Test [Web.Hosting.Resources] - End to end: ";
    private const string shop = "https://shop.example";
    private const string partner = "https://partner.example";

    [Fact(DisplayName = DisplayPrefix + "A managed instance publishes its manifest before it serves")]
    public async Task Start_WithGateway_ShouldPublishItsManifest()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });

        // Assert
        ResourceManifest manifest = gateway.Manifests.ShouldHaveSingleItem();
        manifest.Provider.ShouldBe((ProviderName)"Web");
        manifest.Name.ShouldBe((ResourceName)"orders-api");
        manifest.Key.ShouldBe(instance.Resource.Key);
        manifest.Actions.ShouldHaveSingleItem().ShouldBe(new ResourceActionDescriptor("cors", ResourceActionKind.ReadWrite, IsStateful: true));
    }

    [Fact(DisplayName = DisplayPrefix + "A managed instance runs one resource service, whose control plane published the manifest")]
    public async Task Start_WithGateway_ShouldRunOneResourceService()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });

        // Assert
        IResourceService service = instance.ResourceServices.ShouldHaveSingleItem();
        service.ControlPlane.Resource.ShouldBeSameAs(instance.Resource);
        service.ControlPlane.Manifest.Key.ShouldBe(gateway.Manifests.ShouldHaveSingleItem().Key);
    }

    [Fact(DisplayName = DisplayPrefix + "An unmanaged instance runs no resource service")]
    public async Task Start_WithoutManifestEndpoint_ShouldRunNoResourceService()
    {
        // Act
        await using RunningResource instance = await RunningResource.StartAsync(manifestEndpoint: null, new WebCorsPolicy { Origins = [shop] });

        // Assert
        instance.ResourceServices.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A platform's own serializer writes the publication and the control plane's content")]
    public async Task Start_WithCustomSerializer_ShouldUseItEndToEnd()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        var serializer = new RecordingSerializer();
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] },
            resource => resource.Serializer = serializer);

        // Act
        using HttpResponseMessage response = await instance.ControlPlane.GetAsync("actions/cors");

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(serializer.MediaType);
        serializer.Written.ShouldContain(typeof(ResourceManifest));
        serializer.Written.ShouldContain(typeof(WebCorsPolicy));
        serializer.Written.ShouldContain(typeof(ResourceActionResult));
        gateway.Manifests.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "The HTTP client callback supplies the client the instance publishes with")]
    public async Task Start_WithHttpClientFactory_ShouldPublishWithItsClient()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        int created = 0;

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] },
            resource => resource.HttpClientFactory = () =>
            {
                Interlocked.Increment(ref created);
                return new HttpClient();
            });

        // Assert
        created.ShouldBe(1);
        gateway.Manifests.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "A managed instance renews its registration while it runs")]
    public async Task Start_WithLease_ShouldRenewTheRegistration()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        gateway.LeaseWith(TimeSpan.FromMilliseconds(300));

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });
        await Eventually.WaitUntilAsync(() => gateway.Renewals >= 2);

        // Assert
        gateway.Manifests.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "An instance its gateway forgot publishes its manifest again")]
    public async Task Renewal_WhenGatewayForgetsTheInstance_ShouldPublishAgain()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        gateway.LeaseWith(TimeSpan.FromMilliseconds(300));
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });

        // Act
        gateway.ForgetInstances();
        await Eventually.WaitUntilAsync(() => gateway.Manifests.Count >= 2);

        // Assert
        gateway.Manifests.ShouldAllBe(manifest => manifest.Key == instance.Resource.Key);
    }

    [Fact(DisplayName = DisplayPrefix + "The gateway reads the CORS policy in effect")]
    public async Task CorsRead_FromGateway_ShouldReturnThePolicyInEffect()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });

        // Act
        using HttpResponseMessage response = await instance.ControlPlane.GetAsync("actions/cors");

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        using JsonDocument envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        envelope.RootElement.GetProperty("status").GetString().ShouldBe("Succeeded");
        envelope.RootElement.GetProperty("output").GetProperty("origins")[0].GetString().ShouldBe(shop);
    }

    [Fact(DisplayName = DisplayPrefix + "The gateway's write changes the origins the application allows")]
    public async Task CorsWrite_FromGateway_ShouldChangeTheAllowedOrigin()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });
        (await instance.GetAllowedOriginAsync(shop)).ShouldBe(shop);
        (await instance.GetAllowedOriginAsync(partner)).ShouldBeNull();

        // Act
        using HttpResponseMessage response = await instance.ControlPlane.PostAsync("actions/cors",
            new StringContent($$"""{"origins":["{{partner}}"]}""", Encoding.UTF8, "application/json"));

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        using JsonDocument envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        envelope.RootElement.GetProperty("status").GetString().ShouldBe("Succeeded");
        envelope.RootElement.GetProperty("state").GetProperty("action").GetString().ShouldBe("cors");
        envelope.RootElement.GetProperty("state").GetProperty("value").GetProperty("origins")[0].GetString().ShouldBe(partner);
        (await instance.GetAllowedOriginAsync(partner)).ShouldBe(partner);
        (await instance.GetAllowedOriginAsync(shop)).ShouldBeNull();
        instance.Resource.CorsPolicy.ShouldNotBeNull().Origins.ShouldBe([partner]);
    }

    [Theory(DisplayName = DisplayPrefix + "A write the CORS rules refuse is rejected and the policy in effect stays")]
    [InlineData("""{"origins":["not an origin"]}""")]
    [InlineData("""{"origins":["*"],"allowCredentials":true}""")]
    public async Task CorsWrite_WithInvalidPolicy_ShouldBeRejected(string policy)
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });

        // Act
        using HttpResponseMessage response = await instance.ControlPlane.PostAsync("actions/cors",
            new StringContent(policy, Encoding.UTF8, "application/json"));

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        using JsonDocument envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        envelope.RootElement.GetProperty("status").GetString().ShouldBe("Rejected");
        envelope.RootElement.GetProperty("reason").GetString().ShouldNotBeNullOrWhiteSpace();
        (await instance.GetAllowedOriginAsync(shop)).ShouldBe(shop);
    }

    [Fact(DisplayName = DisplayPrefix + "A new instance restores the policy the gateway kept before it serves")]
    public async Task Start_WithStoredSnapshot_ShouldServeTheRestoredPolicy()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        gateway.AnswerWith($$$"""[{"action":"cors","value":{"origins":["{{{partner}}}"]}}]""");

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });

        // Assert
        (await instance.GetAllowedOriginAsync(partner)).ShouldBe(partner);
        (await instance.GetAllowedOriginAsync(shop)).ShouldBeNull();
        instance.Resource.CorsPolicy.ShouldNotBeNull().Origins.ShouldBe([partner]);
    }

    [Fact(DisplayName = DisplayPrefix + "The policy one instance's write leaves is what the next instance starts with")]
    public async Task CorsWrite_ThenNewInstance_ShouldCarryThePolicyForward()
    {
        // Arrange — the gateway keeps the snapshot a write returns, as the application model will.
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        string snapshot;
        await using (RunningResource first = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] }))
        {
            using HttpResponseMessage write = await first.ControlPlane.PostAsync("actions/cors",
                new StringContent($$"""{"origins":["{{partner}}"]}""", Encoding.UTF8, "application/json"));
            using JsonDocument envelope = JsonDocument.Parse(await write.Content.ReadAsStringAsync());
            snapshot = envelope.RootElement.GetProperty("state").GetRawText();
        }

        gateway.AnswerWith("[" + snapshot + "]");

        // Act
        await using RunningResource second = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });

        // Assert
        gateway.Manifests.Count.ShouldBe(2);
        (await second.GetAllowedOriginAsync(partner)).ShouldBe(partner);
    }

    [Fact(DisplayName = DisplayPrefix + "The control plane serves the manifest it published")]
    public async Task ControlPlane_GetManifest_ShouldReturnThePublishedManifest()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });

        // Act
        using HttpResponseMessage response = await instance.ControlPlane.GetAsync("manifest");

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        ResourceManifest? manifest = JsonResourceContentSerializer.Create().Deserialize<ResourceManifest>(await response.Content.ReadAsByteArrayAsync());
        manifest.ShouldNotBeNull().Key.ShouldBe(instance.Resource.Key);
    }

    [Fact(DisplayName = DisplayPrefix + "The control plane answers 404 for an action the resource does not have")]
    public async Task ControlPlane_UnknownAction_ShouldReturnNotFound()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        await using RunningResource instance = await RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] });

        // Act
        using HttpResponseMessage response = await instance.ControlPlane.GetAsync("actions/log-level");

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway that refuses the manifest fails the application's start")]
    public async Task Start_WhenGatewayRefuses_ShouldFailToStart()
    {
        // Arrange
        await using GatewayStandIn gateway = await GatewayStandIn.StartAsync();
        gateway.RefuseWith(503);

        // Act / Assert
        await Should.ThrowAsync<Exception>(() => RunningResource.StartAsync(gateway.ManifestEndpoint, new WebCorsPolicy { Origins = [shop] }));
        gateway.Manifests.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "Without a manifest endpoint the application runs unmanaged with its code-defined policy")]
    public async Task Start_WithoutManifestEndpoint_ShouldRunUnmanaged()
    {
        // Act
        await using RunningResource instance = await RunningResource.StartAsync(manifestEndpoint: null, new WebCorsPolicy { Origins = [shop] });

        // Assert
        (await instance.GetAllowedOriginAsync(shop)).ShouldBe(shop);
        await Should.ThrowAsync<HttpRequestException>(() => instance.ControlPlane.GetAsync("manifest"));
    }
}
