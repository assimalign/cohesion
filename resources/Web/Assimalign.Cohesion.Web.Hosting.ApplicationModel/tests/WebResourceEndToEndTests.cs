using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Web.Hosting.ApplicationModel.Internal;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Tests;

/// <summary>
/// A Web resource instance on a throwaway platform: the instance publishes its manifest through the
/// platform's client, the gateway reads and writes its CORS policy through the control plane the
/// client carries its calls to, and a new instance starts with the policy the gateway kept.
/// </summary>
public sealed class WebResourceEndToEndTests
{
    private const string DisplayPrefix = "Cohesion Test [Web.Hosting.ApplicationModel] - End to end: ";
    private const string shop = "https://shop.example";
    private const string partner = "https://partner.example";

    [Fact(DisplayName = DisplayPrefix + "A managed instance publishes its manifest through its platform's client before it serves")]
    public async Task Start_WithGateway_ShouldPublishItsManifest()
    {
        // Arrange
        var gateway = new TestGatewayClient();

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });

        // Assert: the resource names no address of its own; where the gateway reaches it is the platform's.
        ApplicationInstanceManifest manifest = gateway.Manifests.ShouldHaveSingleItem();
        manifest.Provider.ShouldBe((ProviderName)"Web");
        manifest.Name.ShouldBe((ResourceName)"orders-api");
        manifest.Key.ShouldBe(instance.Resource.Key);
        manifest.ControlPlaneEndpoint.ShouldBeNull();
        manifest.Actions.ShouldHaveSingleItem().ShouldBe(new ApplicationActionDescriptor("cors", ApplicationActionKind.ReadWrite, IsStateful: true));
    }

    [Fact(DisplayName = DisplayPrefix + "An application that registers no CORS exposes no cors action")]
    public async Task Start_WithoutAddCors_ShouldPublishNoCorsAction()
    {
        // Arrange
        var gateway = new TestGatewayClient();

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway, cors: null);

        // Assert
        gateway.Manifests.ShouldHaveSingleItem().Actions.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A managed instance runs one resource service, whose control plane published the manifest and serves the gateway")]
    public async Task Start_WithGateway_ShouldRunOneResourceService()
    {
        // Arrange
        var gateway = new TestGatewayClient();

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });

        // Assert
        IApplicationInstanceService service = instance.InstanceServices.ShouldHaveSingleItem();
        service.ControlPlane.Instance.ShouldBeSameAs(instance.Resource);
        service.ControlPlane.Manifest.Key.ShouldBe(gateway.Manifests.ShouldHaveSingleItem().Key);
        gateway.Serving.ShouldBeSameAs(service.ControlPlane);
    }

    [Fact(DisplayName = DisplayPrefix + "An instance whose environment names no platform runs no resource service")]
    public async Task Start_WithoutPlatform_ShouldRunNoResourceService()
    {
        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway: null, new WebCorsPolicy { Origins = [shop] });

        // Assert
        instance.InstanceServices.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A platform the resource did not opt into fails the application's build")]
    public async Task Start_OnPlatformWithoutClient_ShouldFailTheBuild()
    {
        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => RunningResource.StartAsync(
            gateway: null,
            new WebCorsPolicy { Origins = [shop] },
            resource => resource.Platform = "kubernetes"));

        // Assert
        exception.Message.ShouldContain("'kubernetes'", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "An action the application adds is published and served beside the resource's own")]
    public async Task AddAction_WithJsonContext_ShouldPublishAndServeTheAction()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] },
            resource => resource.AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        // Act
        ApplicationActionResult result = await gateway.ExecuteAsync("greeting", ApplicationActionOperation.Write, """{"text":"welcome"}""");

        // Assert
        gateway.Manifests.ShouldHaveSingleItem().Actions.Select(static action => action.Name)
            .ShouldBe(new ActionName[] { "cors", "greeting" });
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        ApplicationActionSnapshot state = result.State.ShouldNotBeNull();
        state.Action.ShouldBe((ActionName)"greeting");
        using JsonDocument value = JsonDocument.Parse(state.Value);
        value.RootElement.GetProperty("text").GetString().ShouldBe("welcome");
    }

    [Fact(DisplayName = DisplayPrefix + "An action factory creates the action from the built application's services")]
    public async Task AddAction_WithFactory_ShouldCreateTheActionFromTheApplication()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        await using RunningResource instance = await RunningResource.StartAsync(gateway, cors: null,
            resource => resource.AddAction(
                services => new GreetingAction(services.GetRequiredService<Greeting>().Text),
                GreetingJsonContext.Default),
            beforeResource: builder => builder.Services.AddSingleton(new Greeting("from the application")));

        // Act
        ApplicationActionResult result = await gateway.ExecuteAsync("greeting", ApplicationActionOperation.Read);

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("text").GetString().ShouldBe("from the application");
    }

    [Fact(DisplayName = DisplayPrefix + "A new instance restores an added action's state through the application's context")]
    public async Task AddAction_WithGatewaySnapshot_ShouldRestoreTheAddedAction()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        gateway.AnswerWith("greeting", """{"text":"restored"}""");

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway, cors: null,
            resource => resource.AddAction(new GreetingAction("hello"), GreetingJsonContext.Default));

        // Assert
        ApplicationActionResult result = await gateway.ExecuteAsync("greeting", ApplicationActionOperation.Read);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("text").GetString().ShouldBe("restored");
    }

    [Fact(DisplayName = DisplayPrefix + "An added action named like one of the resource's own fails the application's start")]
    public async Task AddAction_WithTakenName_ShouldFailTheStart()
    {
        // Arrange
        var gateway = new TestGatewayClient();

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(() => RunningResource.StartAsync(
            gateway,
            new WebCorsPolicy { Origins = [shop] },
            resource => resource.AddAction(new GreetingAction(name: "cors"), GreetingJsonContext.Default)));

        // Assert
        exception.Message.ShouldContain("cors", Case.Sensitive);
        gateway.Manifests.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A managed instance renews its registration while it runs")]
    public async Task Start_WithLease_ShouldRenewTheRegistration()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        gateway.LeaseWith(TimeSpan.FromMilliseconds(300));

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });
        await Eventually.WaitUntilAsync(() => gateway.Renewals >= 2);

        // Assert
        gateway.Manifests.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "An instance its gateway forgot publishes its manifest again")]
    public async Task Renewal_WhenGatewayForgetsTheInstance_ShouldPublishAgain()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        gateway.LeaseWith(TimeSpan.FromMilliseconds(300));
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });

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
        var gateway = new TestGatewayClient();
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });

        // Act
        ApplicationActionResult result = await gateway.ExecuteAsync("cors", ApplicationActionOperation.Read);

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("origins")[0].GetString().ShouldBe(shop);
    }

    [Fact(DisplayName = DisplayPrefix + "The gateway's write changes the origins the application allows")]
    public async Task CorsWrite_FromGateway_ShouldChangeTheAllowedOrigin()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });
        (await instance.GetAllowedOriginAsync(shop)).ShouldBe(shop);
        (await instance.GetAllowedOriginAsync(partner)).ShouldBeNull();

        // Act
        ApplicationActionResult result = await gateway.ExecuteAsync("cors", ApplicationActionOperation.Write, $$"""{"origins":["{{partner}}"]}""");

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        ApplicationActionSnapshot state = result.State.ShouldNotBeNull();
        state.Action.ShouldBe((ActionName)"cors");
        using JsonDocument value = JsonDocument.Parse(state.Value);
        value.RootElement.GetProperty("origins")[0].GetString().ShouldBe(partner);
        (await instance.GetAllowedOriginAsync(partner)).ShouldBe(partner);
        (await instance.GetAllowedOriginAsync(shop)).ShouldBeNull();
        instance.DefaultCorsPolicy.Origins.ShouldBe([partner]);
    }

    [Theory(DisplayName = DisplayPrefix + "A write the CORS rules refuse is rejected and the policy in effect stays")]
    [InlineData("""{"origins":["not an origin"]}""")]
    [InlineData("""{"origins":["*"],"allowCredentials":true}""")]
    public async Task CorsWrite_WithInvalidPolicy_ShouldBeRejected(string policy)
    {
        // Arrange
        var gateway = new TestGatewayClient();
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });

        // Act
        ApplicationActionResult result = await gateway.ExecuteAsync("cors", ApplicationActionOperation.Write, policy);

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Rejected);
        result.Reason.ShouldNotBeNullOrWhiteSpace();
        result.State.ShouldBeNull();
        (await instance.GetAllowedOriginAsync(shop)).ShouldBe(shop);
    }

    [Fact(DisplayName = DisplayPrefix + "A new instance restores the policy the gateway kept before it serves")]
    public async Task Start_WithStoredSnapshot_ShouldServeTheRestoredPolicy()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        gateway.AnswerWith("cors", $$"""{"origins":["{{partner}}"]}""");

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });

        // Assert
        (await instance.GetAllowedOriginAsync(partner)).ShouldBe(partner);
        (await instance.GetAllowedOriginAsync(shop)).ShouldBeNull();
        instance.DefaultCorsPolicy.Origins.ShouldBe([partner]);
    }

    [Fact(DisplayName = DisplayPrefix + "A service registered ahead of the resource starts only once the gateway's state is restored")]
    public async Task Start_WithServiceRegisteredFirst_ShouldStartItAfterTheRestore()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        gateway.AnswerWith("cors", $$"""{"origins":["{{partner}}"]}""");
        StartProbe? probe = null;

        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] },
            beforeResource: builder => builder.AddService(context => probe = new StartProbe(context)));

        // Assert
        probe.ShouldNotBeNull().PolicyAtStart.ShouldNotBeNull().Origins.ShouldBe([partner]);
    }

    [Fact(DisplayName = DisplayPrefix + "The policy one instance's write leaves is what the next instance starts with")]
    public async Task CorsWrite_ThenNewInstance_ShouldCarryThePolicyForward()
    {
        // Arrange — the gateway keeps the snapshot a write returns, as the application model does.
        var gateway = new TestGatewayClient();
        ApplicationActionSnapshot snapshot;
        await using (RunningResource first = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] }))
        {
            ApplicationActionResult write = await gateway.ExecuteAsync("cors", ApplicationActionOperation.Write, $$"""{"origins":["{{partner}}"]}""");
            snapshot = write.State.ShouldNotBeNull();
        }

        gateway.AnswerWith(snapshot);

        // Act
        await using RunningResource second = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });

        // Assert
        gateway.Manifests.Count.ShouldBe(2);
        (await second.GetAllowedOriginAsync(partner)).ShouldBe(partner);
    }

    [Fact(DisplayName = DisplayPrefix + "The gateway's calls reach the control plane only while the instance runs")]
    public async Task Stop_ShouldCloseTheControlPlaneToTheGateway()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });
        IApplicationInstanceControlPlane? serving = gateway.Serving;

        // Act
        await instance.DisposeAsync();

        // Assert
        serving.ShouldNotBeNull();
        gateway.Serving.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "An action the resource does not have is not found")]
    public async Task Execute_UnknownAction_ShouldReturnNotFound()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        await using RunningResource instance = await RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] });

        // Act
        ApplicationActionResult result = await gateway.ExecuteAsync("log-level", ApplicationActionOperation.Read);

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.NotFound);
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway that refuses the manifest fails the application's start")]
    public async Task Start_WhenGatewayRefuses_ShouldFailToStart()
    {
        // Arrange
        var gateway = new TestGatewayClient();
        gateway.RefuseWith(new InvalidOperationException("The gateway refused the manifest."));

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(() => RunningResource.StartAsync(gateway, new WebCorsPolicy { Origins = [shop] }));
        gateway.Manifests.ShouldHaveSingleItem();
        gateway.Serving.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "Without a named platform the application runs unmanaged with its code-defined policy")]
    public async Task Start_WithoutPlatform_ShouldRunUnmanaged()
    {
        // Act
        await using RunningResource instance = await RunningResource.StartAsync(gateway: null, new WebCorsPolicy { Origins = [shop] });

        // Assert
        (await instance.GetAllowedOriginAsync(shop)).ShouldBe(shop);
        instance.InstanceServices.ShouldBeEmpty();
    }
}
