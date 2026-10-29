using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class ApplicationGatewayTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway: ";

    private static readonly TimeSpan _shortWindow = TimeSpan.FromMilliseconds(300);

    [Fact(DisplayName = DisplayPrefix + "A gateway deploys the resources its model declares")]
    public async Task RunAsync_WithModel_ShouldDeployItsResources()
    {
        // Arrange
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("IdentityHub", "identity");
        IApplicationModel model = builder.Build();
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway(model);
        using var stop = new CancellationTokenSource();

        // Act
        Task run = gateway.RunAsync(stop.Token);
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await run;

        // Assert
        gateway.Name.ShouldBe(new GatewayName("root"));
        gateway.Model.ShouldBeSameAs(model);
        model.Role.ShouldBe(ApplicationGatewayRole.Root);
        infrastructure.Deployed.ShouldBe(["root:IdentityHub/identity"]);
        gateway.Resources.ShouldHaveSingleItem().Instances.Count.ShouldBe(1);
        infrastructure.TornDown.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A resource that misses the startup window is torn down and fails the run")]
    public async Task RunAsync_WhenResourceMissesStartupWindow_ShouldTearItDownAndFail()
    {
        // Arrange
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        builder.AddResource("Web", "customers-api");
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway(builder.Build(), _shortWindow);
        infrastructure.Silent.Add("customers-api");
        ApplicationGatewayException? lateInstance = null;
        infrastructure.OnTeardown = async resource =>
        {
            // An instance that starts during teardown is refused, not registered.
            lateInstance = await Should.ThrowAsync<ApplicationGatewayException>(() => infrastructure.StartInstanceAsync(resource.Provider, resource.Name));
        };

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.RunAsync());

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.StartupTimedOut);
        infrastructure.TornDown.Select(name => name.Value).ShouldBe(["customers-api"]);
        lateInstance.ShouldNotBeNull().ErrorCode.ShouldBe(ApplicationGatewayErrorCode.ResourceTornDown);
        gateway.Resources.Single(resource => resource.Manifest.Name.Value == "customers-api").Instances.ShouldBeEmpty();
        await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.Started);
    }

    [Fact(DisplayName = DisplayPrefix + "Cancelling during startup stops the gateway without tearing anything down")]
    public async Task RunAsync_CancelledDuringStartup_ShouldStopWithoutTeardown()
    {
        // Arrange
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway(builder.Build(), TimeSpan.FromMinutes(5));
        infrastructure.Silent.Add("orders-api");
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        await gateway.RunAsync(stop.Token);

        // Assert
        infrastructure.TornDown.ShouldBeEmpty();
        gateway.Started.IsCanceled.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Publications are refused before the gateway runs and after it stops")]
    public async Task PublishAsync_OutsideRun_ShouldThrowPublicationsClosed()
    {
        // Arrange
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        (ApplicationGateway gateway, _) = CreateGateway(builder.Build());
        var manifest = new ApplicationInstanceManifest("Web", "orders-api", InstanceKey.New(), []);
        ApplicationGatewayException before = Should.Throw<ApplicationGatewayException>(() => gateway.PublishAsync(manifest));
        using var stop = new CancellationTokenSource();
        Task run = gateway.RunAsync(stop.Token);
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await run;

        // Act
        ApplicationGatewayException after = Should.Throw<ApplicationGatewayException>(() => gateway.PublishAsync(manifest));

        // Assert
        before.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.PublicationsClosed);
        after.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.PublicationsClosed);
    }

    [Fact(DisplayName = DisplayPrefix + "A publication for a resource the gateway does not run is refused")]
    public async Task PublishAsync_ForUnknownResource_ShouldThrowResourceUnknown()
    {
        // Arrange
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("IdentityHub", "identity");
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway(builder.Build());
        using var stop = new CancellationTokenSource();
        Task run = gateway.RunAsync(stop.Token);
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(
            () => infrastructure.StartInstanceAsync("Web", "orders-api"));
        stop.Cancel();
        await run;

        // Assert: the root gateway does not run orders-api.
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.ResourceUnknown);
    }

    [Fact(DisplayName = DisplayPrefix + "A write recorded on one instance reaches running instances and those started later")]
    public async Task PublishAsync_AfterRecordedWrite_ShouldRestoreEveryInstance()
    {
        // Arrange
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway(builder.Build());
        using var stop = new CancellationTokenSource();
        Task run = gateway.RunAsync(stop.Token);
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));
        TestInstance second = await infrastructure.StartInstanceAsync("Web", "orders-api");
        IApplicationResourceDeployment ordersApi = gateway.Resources.Single();
        (TestInstance first, ApplicationInstanceService firstService) = infrastructure.Instances.Values.First(entry => entry.Instance.Key != second.Key);

        // Act: the write runs once; the gateway records it and restores it on the others.
        ApplicationActionResult write = await firstService.ControlPlane.ExecuteAsync(
            "log-level",
            ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json, """{"Level":"Debug"}"""u8.ToArray()));
        foreach (InstanceKey target in ordersApi.Record(first.Key, write.State!))
        {
            await infrastructure.Instances[target].Service.ControlPlane.RestoreAsync(write.State!);
        }

        TestInstance scaledOut = await infrastructure.StartInstanceAsync("Web", "orders-api");
        stop.Cancel();
        await run;

        // Assert
        second.Level.Level.ShouldBe("Debug");
        scaledOut.Level.Level.ShouldBe("Debug");
        ordersApi.Instances.Count.ShouldBe(3);
    }

    [Fact(DisplayName = DisplayPrefix + "The infrastructure's actions are exposed on each resource")]
    public async Task Resources_WithInfrastructureActions_ShouldDispatchThem()
    {
        // Arrange
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway(builder.Build());
        using var stop = new CancellationTokenSource();
        Task run = gateway.RunAsync(stop.Token);
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));
        IApplicationResourceDeployment ordersApi = gateway.Resources.Single();

        // Act
        ApplicationActionResult scaled = await ordersApi.ExecuteAsync("scale", ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json, """{"Replicas":3}"""u8.ToArray()));
        ApplicationActionResult read = await ordersApi.ExecuteAsync("scale", ApplicationActionContext.Create(ApplicationActionKind.Read, TestSerializer.Json));
        ApplicationActionResult unknown = await ordersApi.ExecuteAsync("restart", ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json));
        ApplicationActionResult rejected = await ordersApi.ExecuteAsync("scale", ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json, "not json"u8.ToArray()));
        stop.Cancel();
        await run;

        // Assert
        ordersApi.Actions.ShouldHaveSingleItem().Name.ShouldBe(new ActionName("scale"));
        scaled.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        infrastructure.Scale.Replicas.ShouldBe(3);
        read.Status.ShouldBe(ApplicationActionStatus.NotSupported);
        unknown.Status.ShouldBe(ApplicationActionStatus.NotFound);
        rejected.Status.ShouldBe(ApplicationActionStatus.Rejected);
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway runs once")]
    public async Task RunAsync_Twice_ShouldThrow()
    {
        // Arrange
        (ApplicationGateway gateway, _) = CreateGateway(ApplicationModel.CreateBuilder("root").Build());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await gateway.RunAsync(stop.Token);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => gateway.RunAsync(CancellationToken.None));
    }

    private static (ApplicationGateway Gateway, TestInfrastructure Infrastructure) CreateGateway(IApplicationModel model, TimeSpan? startupTimeout = null)
    {
        var infrastructure = new TestInfrastructure();
        ApplicationGateway gateway = RunningGateway.Create(infrastructure, new InMemoryApplicationGatewayStore(), model, startupTimeout);
        return (gateway, infrastructure);
    }
}
