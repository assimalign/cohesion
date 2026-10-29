using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A gateway restarted or redeployed over a platform that still runs what the previous gateway
/// deployed, with its record in a JSON file.
/// </summary>
public sealed class ApplicationGatewayReconciliationTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway reconciliation: ";

    private static readonly TimeSpan _shortWindow = TimeSpan.FromMilliseconds(300);

    private readonly TemporaryDirectory _directory = new();
    private readonly TestInfrastructure _infrastructure = new();
    private readonly JsonFileApplicationGatewayStore _store;

    public ApplicationGatewayReconciliationTests()
    {
        _store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "A restarted gateway adopts what runs instead of deploying it again")]
    public async Task RunAsync_RestartedWithSameModel_ShouldAdoptWithoutRedeploying()
    {
        // Arrange: the first gateway deploys orders-api and records a write.
        IApplicationModel model = CreateModel(["orders-api"]);
        await using (RunningGateway first = await StartAsync(model))
        {
            await WriteLogLevelAsync(first.Gateway, "Debug");
        }

        // Act: a window no new instance could meet, since the platform starts none.
        _infrastructure.Silent.Add("orders-api");
        await using RunningGateway second = await StartAsync(model, _shortWindow);

        // Assert
        _infrastructure.Deployed.ShouldHaveSingleItem();
        _infrastructure.TornDown.ShouldBeEmpty();
        IApplicationResourceDeployment ordersApi = second.Gateway.Resources.ShouldHaveSingleItem();
        ordersApi.Instances.ShouldHaveSingleItem();
        ordersApi.State.ShouldHaveSingleItem().Action.ShouldBe((ActionName)"log-level");
    }

    [Fact(DisplayName = DisplayPrefix + "An instance started after a restart restores the state recorded before it")]
    public async Task PublishAsync_AfterRestart_ShouldRestoreRecordedState()
    {
        // Arrange
        IApplicationModel model = CreateModel(["orders-api"]);
        await using (RunningGateway first = await StartAsync(model))
        {
            await WriteLogLevelAsync(first.Gateway, "Debug");
        }

        await using RunningGateway second = await StartAsync(model);

        // Act: the platform scales out after the restart.
        TestInstance scaledOut = await _infrastructure.StartInstanceAsync("Web", "orders-api");

        // Assert
        scaledOut.Level.Level.ShouldBe("Debug");
        second.Gateway.Resources.Single().Instances.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "Resources the model no longer declares are torn down after startup")]
    public async Task RunAsync_WhenModelDropsResources_ShouldRetireThem()
    {
        // Arrange
        await using (await StartAsync(CreateModel(["orders-api", "customers-api"])))
        {
        }

        // Act
        await using RunningGateway second = await StartAsync(CreateModel(["orders-api"]));

        // Assert
        _infrastructure.TornDown.Select(name => name.Value).ShouldBe(["customers-api"]);
        ApplicationGatewayRecord record = (await _store.LoadAsync("root")).ShouldNotBeNull();
        record.Resources.Select(resource => resource.Name.Value).ShouldBe(["orders-api"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A new revision redeploys the resource without the state recorded for the previous one")]
    public async Task RunAsync_WithNewRevision_ShouldRedeployWithoutRecordedState()
    {
        // Arrange
        IApplicationModel model = CreateModel(["orders-api"]);
        await using (RunningGateway first = await StartAsync(model))
        {
            await WriteLogLevelAsync(first.Gateway, "Debug");
        }

        _infrastructure.Revisions["orders-api"] = "2";

        // Act
        await using RunningGateway second = await StartAsync(model);

        // Assert
        _infrastructure.Deployed.Count.ShouldBe(2);
        IApplicationResourceDeployment ordersApi = second.Gateway.Resources.Single();
        ordersApi.State.ShouldBeEmpty();
        ApplicationInstanceManifest instance = ordersApi.Instances.ShouldHaveSingleItem();
        _infrastructure.Instances[instance.Key].Instance.Level.Level.ShouldBe("Information");
    }

    [Fact(DisplayName = DisplayPrefix + "A workload that disappeared is created again with the state recorded for it")]
    public async Task RunAsync_WhenWorkloadDisappeared_ShouldRecreateItWithRecordedState()
    {
        // Arrange
        IApplicationModel model = CreateModel(["orders-api"]);
        await using (RunningGateway first = await StartAsync(model))
        {
            await WriteLogLevelAsync(first.Gateway, "Debug");
        }

        _infrastructure.Running.TryRemove("orders-api", out _);

        // Act
        await using RunningGateway second = await StartAsync(model);

        // Assert
        _infrastructure.Deployed.Count.ShouldBe(2);
        IApplicationResourceDeployment ordersApi = second.Gateway.Resources.Single();
        ordersApi.State.ShouldHaveSingleItem();
        ApplicationInstanceManifest instance = ordersApi.Instances.ShouldHaveSingleItem();
        _infrastructure.Instances[instance.Key].Instance.Level.Level.ShouldBe("Debug");
    }

    [Fact(DisplayName = DisplayPrefix + "An update that misses the startup window fails the run and is not torn down")]
    public async Task RunAsync_WhenUpdateMissesStartupWindow_ShouldFailWithoutTeardown()
    {
        // Arrange
        IApplicationModel model = CreateModel(["orders-api"]);
        await using (await StartAsync(model))
        {
        }

        _infrastructure.Revisions["orders-api"] = "2";
        _infrastructure.Silent.Add("orders-api");
        ApplicationGateway gateway = RunningGateway.Create(_infrastructure, _store, model, _shortWindow);

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.RunAsync());

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.StartupTimedOut);
        _infrastructure.TornDown.ShouldBeEmpty();
        _infrastructure.Running.ShouldContainKey("orders-api");
    }

    [Fact(DisplayName = DisplayPrefix + "The record holds each resource's revision, state, and instances")]
    public async Task RecordAsync_ShouldSaveTheGatewayRecord()
    {
        // Arrange
        IApplicationModel model = CreateModel(["orders-api"]);
        await using RunningGateway running = await StartAsync(model);

        // Act
        await WriteLogLevelAsync(running.Gateway, "Warning");

        // Assert
        ApplicationGatewayRecord record = (await _store.LoadAsync("root")).ShouldNotBeNull();
        ApplicationResourceRecord ordersApi = record.Resources.ShouldHaveSingleItem();
        ordersApi.Revision.ShouldEndWith(":1");
        ordersApi.Instances.ShouldHaveSingleItem();
        Encoding.UTF8.GetString(ordersApi.State.ShouldHaveSingleItem().Value.Span).ShouldBe("""{"Level":"Warning"}""");
    }

    private Task<RunningGateway> StartAsync(IApplicationModel model, TimeSpan? startupTimeout = null)
    {
        return RunningGateway.StartAsync(_infrastructure, _store, model, startupTimeout);
    }

    // Writes the level on the platform's first orders-api instance and records it through the
    // gateway, which restores it on the others.
    private async Task WriteLogLevelAsync(ApplicationGateway gateway, string level)
    {
        (TestInstance instance, ApplicationInstanceService service) = _infrastructure.Instances.Values.First();
        ApplicationActionResult write = await service.ControlPlane.ExecuteAsync(
            "log-level",
            ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json, Encoding.UTF8.GetBytes($$"""{"Level":"{{level}}"}""")));

        foreach (InstanceKey target in await gateway.RecordAsync("Web", "orders-api", instance.Key, write.State!))
        {
            await _infrastructure.Instances[target].Service.ControlPlane.RestoreAsync(write.State!);
        }
    }

    private static IApplicationModel CreateModel(string[] resources)
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        foreach (string resource in resources)
        {
            builder.AddResource("Web", resource);
        }

        return builder.Build();
    }
}
