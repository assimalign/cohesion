using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.Tests;

public class InProcessGatewayClientTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting.InProcess] - In-process gateway client: ";

    [Fact(DisplayName = DisplayPrefix + "A resource that opts in uses the in-process client when its environment names the platform")]
    public void UseInProcess_InInProcessEnvironment_ShouldBeTheSelectedClient()
    {
        // Act
        TestOptions options = OptIn(TestGateway.CreateEndpoint().AbsoluteUri);

        // Assert
        IApplicationInstanceGatewayClient client = options.GetGatewayClient().ShouldNotBeNull();
        client.Platform.ShouldBe(InProcessPlatform.Name);
        InProcessPlatform.Name.ShouldBe((PlatformName)"inprocess");
    }

    [Fact(DisplayName = DisplayPrefix + "A resource opts into the platform once")]
    public void UseInProcess_Twice_ShouldThrow()
    {
        // Arrange
        TestOptions options = OptIn(TestGateway.CreateEndpoint().AbsoluteUri);

        // Act & Assert
        Should.Throw<ArgumentException>(() => options.UseInProcess());
    }

    [Fact(DisplayName = DisplayPrefix + "A publication reaches the gateway at the endpoint and names that endpoint as the control plane's")]
    public async Task PublishAsync_ToRegisteredGateway_ShouldNameTheEndpoint()
    {
        // Arrange
        Uri endpoint = TestGateway.CreateEndpoint();
        var gateway = new TestGateway();
        using IDisposable registration = InProcessPlatform.RegisterGateway(endpoint, gateway);
        IApplicationInstanceGatewayClient client = OptIn(endpoint.AbsoluteUri).GetGatewayClient()!;
        IApplicationInstanceControlPlane controlPlane = TestResource.CreateControlPlane();

        // Act
        ApplicationInstancePublication publication = await client.PublishAsync(controlPlane.Manifest);
        bool renewed = await client.RenewAsync(controlPlane.Manifest);

        // Assert: the gateway received the manifest itself, with the endpoint it calls the instance through.
        publication.Snapshots.ShouldBeEmpty();
        renewed.ShouldBeTrue();
        ApplicationInstanceManifest published = gateway.Published.ShouldHaveSingleItem();
        published.ShouldBe(controlPlane.Manifest with { ControlPlaneEndpoint = endpoint });
        gateway.Renewed.ShouldHaveSingleItem().ControlPlaneEndpoint.ShouldBe(endpoint);
    }

    [Fact(DisplayName = DisplayPrefix + "A publication to an endpoint no gateway receives at fails")]
    public async Task PublishAsync_WithoutRegisteredGateway_ShouldThrow()
    {
        // Arrange
        Uri endpoint = TestGateway.CreateEndpoint();
        IApplicationInstanceGatewayClient client = OptIn(endpoint.AbsoluteUri).GetGatewayClient()!;
        IApplicationInstanceControlPlane controlPlane = TestResource.CreateControlPlane();
        InProcessPlatform.RegisterGateway(endpoint, new TestGateway()).Dispose();

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => client.PublishAsync(controlPlane.Manifest).AsTask());

        // Assert
        exception.Message.ShouldContain(endpoint.AbsoluteUri, Case.Sensitive);
    }

    [Theory(DisplayName = DisplayPrefix + "An environment that names no in-process endpoint fails the publication")]
    [InlineData(null)]
    [InlineData("http://127.0.0.1:5199/manifests")]
    [InlineData("not a url")]
    public async Task PublishAsync_WithoutInProcessEndpoint_ShouldThrow(string? endpoint)
    {
        // Arrange
        IApplicationInstanceGatewayClient client = OptIn(endpoint).GetGatewayClient()!;

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => client.PublishAsync(TestResource.CreateControlPlane().Manifest).AsTask());
    }

    [Fact(DisplayName = DisplayPrefix + "Starting attaches the control plane, and the gateway's calls run in the instance's environment")]
    public async Task StartAsync_ThenGatewayCalls_ShouldRunInTheInstancesEnvironment()
    {
        // Arrange
        Uri endpoint = TestGateway.CreateEndpoint();
        var gateway = new TestGateway();
        using IDisposable registration = InProcessPlatform.RegisterGateway(endpoint, gateway);
        IApplicationInstanceControlPlane controlPlane = TestResource.CreateControlPlane();
        IApplicationInstanceGatewayClient client;
        using (EnterInstanceEnvironment(endpoint.AbsoluteUri))
        {
            client = new TestOptions().UseInProcess().GetGatewayClient()!;
            await client.StartAsync(controlPlane);
        }

        // Act: the gateway calls from its own flow, which holds no instance environment.
        IApplicationInstanceControlPlane attached = gateway.Attached.ShouldHaveSingleItem().Value;
        ApplicationActionResult result = await attached.ExecuteAsync("environment", ApplicationActionContext.Create(ApplicationActionOperation.Read, controlPlane.Serializer));

        // Assert
        attached.Manifest.ShouldBeSameAs(controlPlane.Manifest);
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        controlPlane.Serializer.Deserialize<ProgramEnvironment>(result.Output).ShouldNotBeNull().Resource.ShouldBe("orders-api");
        AppEnvironment.Current.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "Stopping detaches the control plane, and the client can start again")]
    public async Task StopAsync_AfterStart_ShouldDetach()
    {
        // Arrange
        Uri endpoint = TestGateway.CreateEndpoint();
        var gateway = new TestGateway();
        using IDisposable registration = InProcessPlatform.RegisterGateway(endpoint, gateway);
        IApplicationInstanceGatewayClient client = OptIn(endpoint.AbsoluteUri).GetGatewayClient()!;
        IApplicationInstanceControlPlane controlPlane = TestResource.CreateControlPlane();
        await client.StartAsync(controlPlane);

        // Act
        await client.StopAsync();
        bool detached = gateway.Attached.IsEmpty;
        await client.StartAsync(controlPlane);

        // Assert
        detached.ShouldBeTrue();
        gateway.Attached.ShouldHaveSingleItem();
        await Should.ThrowAsync<InvalidOperationException>(() => client.StartAsync(controlPlane).AsTask());
    }

    [Fact(DisplayName = DisplayPrefix + "An endpoint is an in-process URL, registered once")]
    public void RegisterGateway_WithInvalidOrTakenEndpoint_ShouldThrow()
    {
        // Arrange
        Uri endpoint = TestGateway.CreateEndpoint();
        using IDisposable registration = InProcessPlatform.RegisterGateway(endpoint, new TestGateway());

        // Act & Assert
        Should.Throw<ArgumentException>(() => InProcessPlatform.RegisterGateway(endpoint, new TestGateway()));
        Should.Throw<ArgumentException>(() => InProcessPlatform.RegisterGateway(new Uri("http://127.0.0.1/"), new TestGateway()));
        Should.Throw<ArgumentNullException>(() => InProcessPlatform.RegisterGateway(endpoint, null!));
    }

    // Opts a resource into the platform in the environment a gateway gives a program it runs.
    private static TestOptions OptIn(string? endpoint)
    {
        using (EnterInstanceEnvironment(endpoint))
        {
            return new TestOptions().UseInProcess();
        }
    }

    private static IDisposable EnterInstanceEnvironment(string? endpoint)
    {
        var variables = new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = "inprocess",
            [AppEnvironment.Variables.Resource] = "orders-api",
        };
        if (endpoint is not null)
        {
            variables[AppEnvironment.Variables.ManifestEndpoint] = endpoint;
        }

        return AppEnvironment.Enter(AppEnvironmentScope.Create(variables, CancellationToken.None));
    }
}
