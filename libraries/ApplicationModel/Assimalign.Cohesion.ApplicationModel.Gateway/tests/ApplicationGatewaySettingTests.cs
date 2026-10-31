using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A gateway filling in each resource's settings from what it depends on, and planning the resource
/// only once those values are known. Settings filled in from a parent gateway are covered by
/// <see cref="ApplicationGatewayJoinTests"/>.
/// </summary>
public sealed class ApplicationGatewaySettingTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway settings: ";

    private const string DatabaseEndpoint = "http://db.root.svc";

    private const string MovedEndpoint = "http://db-moved.root.svc";

    private readonly TemporaryDirectory _directory = new();
    private readonly TestInfrastructure _infrastructure = new();
    private readonly JsonFileApplicationGatewayStore _store;

    public ApplicationGatewaySettingTests()
    {
        _store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "Instances start with their settings, outputs filled in from what the resource depends on")]
    public async Task RunAsync_WithSettings_ShouldDeliverTheirValuesToTheInstances()
    {
        // Arrange
        IApplicationModel model = CreateChain(mode: "fast");

        // Act
        await using RunningGateway running = await StartAsync(model);

        // Assert
        TestInstance api = Instance("api");
        api.Settings["Db:Endpoint"].ShouldBe(DatabaseEndpoint);
        api.Settings["feature:mode"].ShouldBe("fast");
        _infrastructure.Planned["api"].Count.ShouldBe(2);
        ShouldPrecede("outputs:db", "plan:api");
    }

    [Fact(DisplayName = DisplayPrefix + "A dependency's new output updates what uses it, which keeps its recorded state")]
    public async Task RunAsync_WhenDependencyOutputChanges_ShouldUpdateTheDependentAndKeepItsState()
    {
        // Arrange: an operator changed api's log level, then the platform moved db.
        IApplicationModel model = CreateChain(mode: "fast");
        await using (RunningGateway first = await StartAsync(model))
        {
            await WriteLogLevelAsync(first.Gateway, "api", "Debug");
        }

        _infrastructure.Outputs["db"] = new Dictionary<OutputName, string> { [OutputName.Endpoint] = MovedEndpoint };

        // Act
        await using RunningGateway second = await StartAsync(model);

        // Assert: db kept running; api was redeployed with the new address and kept the change.
        _infrastructure.Deployed.ShouldBe(["root:Database.Sql/db", "root:Web/api", "root:Web/api"]);
        TestInstance api = Instance("api", instance => instance.Settings["Db:Endpoint"] == MovedEndpoint);
        api.Level.Level.ShouldBe("Debug");
        Resource(second.Gateway, "api").State.ShouldHaveSingleItem().Action.Value.ShouldBe("log-level");
    }

    [Fact(DisplayName = DisplayPrefix + "A changed value in the model is a new definition, which starts without the recorded state")]
    public async Task RunAsync_WhenDeclaredValueChanges_ShouldStartTheResourceWithoutItsState()
    {
        // Arrange
        await using (RunningGateway first = await StartAsync(CreateChain(mode: "fast")))
        {
            await WriteLogLevelAsync(first.Gateway, "api", "Debug");
        }

        // Act
        await using RunningGateway second = await StartAsync(CreateChain(mode: "safe"));

        // Assert
        TestInstance api = Instance("api", instance => instance.Settings["Feature:Mode"] == "safe");
        api.Level.Level.ShouldBe("Information");
        Resource(second.Gateway, "api").State.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A setting whose output the provider did not report fails the run and is not deployed")]
    public async Task RunAsync_WhenOutputIsNotReported_ShouldFailWithoutDeployingTheResource()
    {
        // Arrange
        _infrastructure.Outputs["db"] = new Dictionary<OutputName, string>();
        ApplicationGateway gateway = CreateGateway(CreateChain(mode: "fast"));

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.RunAsync());

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.OutputMissing);
        exception.Message.ShouldContain("Setting 'Db:Endpoint' of resource 'Web/api' in gateway 'root' uses output 'endpoint' of 'Database.Sql/db'", Case.Sensitive);
        _infrastructure.Deployed.ShouldBe(["root:Database.Sql/db"]);
        _infrastructure.IndexOf("plan:api").ShouldBe(-1);
    }

    [Fact(DisplayName = DisplayPrefix + "An instance that publishes before its resource is planned restores the recorded state")]
    public async Task PublishAsync_BeforeResourceIsPlanned_ShouldRestoreThePreviousState()
    {
        // Arrange: after an operator change on api, db is redefined and its new instance starts only
        // when the test lets it, so api waits to be planned.
        IApplicationModel model = CreateChain(mode: "fast");
        await using (RunningGateway first = await StartAsync(model))
        {
            await WriteLogLevelAsync(first.Gateway, "api", "Debug");
        }

        _infrastructure.Revisions["db"] = "2";
        var release = new TaskCompletionSource();
        _infrastructure.Delayed["db"] = () => release.Task;
        ApplicationGateway gateway = CreateGateway(model);
        using var stop = new CancellationTokenSource();
        Task run = gateway.RunAsync(stop.Token);

        // Act: an instance of the api the platform runs restarts while api waits.
        await Eventually.WaitUntilAsync(() => _infrastructure.Events().Count(call => call == "deploy:db") == 2);
        TestInstance restarted = await _infrastructure.StartInstanceAsync("Web", "api");
        int plans = _infrastructure.Events().Count(call => call == "plan:api");
        release.SetResult();
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await run;

        // Assert: only the first run had planned api; the instance restored the operator's change,
        // and api, unchanged, was adopted.
        plans.ShouldBe(1);
        restarted.Level.Level.ShouldBe("Debug");
        _infrastructure.Deployed.Count(call => call == "root:Web/api").ShouldBe(1);
        Resource(gateway, "api").Instances.ShouldContain(instance => instance.Key == restarted.Key);
    }

    [Fact(DisplayName = DisplayPrefix + "When a plan replaces the workload, recorded instances that renewed meanwhile stay registered")]
    public async Task RunAsync_WhenPlanReplacesWorkload_ShouldDropOnlyRecordedInstancesThatDidNotRenew()
    {
        // Arrange: api runs two instances; then db moves, and its new instance starts only when the
        // test lets it.
        IApplicationModel model = CreateChain(mode: "fast");
        await using (await StartAsync(model))
        {
            await _infrastructure.StartInstanceAsync("Web", "api");
        }

        InstanceKey[] recorded = [.. _infrastructure.Instances.Values.Where(entry => entry.Instance.Name.Value == "api").Select(entry => entry.Instance.Key)];
        _infrastructure.Revisions["db"] = "2";
        _infrastructure.Outputs["db"] = new Dictionary<OutputName, string> { [OutputName.Endpoint] = MovedEndpoint };
        var release = new TaskCompletionSource();
        _infrastructure.Delayed["db"] = () => release.Task;
        ApplicationGateway gateway = CreateGateway(model);
        using var stop = new CancellationTokenSource();
        Task run = gateway.RunAsync(stop.Token);

        // Act: while api waits, one of its recorded instances renews.
        await Eventually.WaitUntilAsync(() => _infrastructure.Events().Count(call => call == "deploy:db") == 2);
        IApplicationGatewayDeployment api = Resource(gateway, "api");
        ApplicationInstanceManifest renewing = api.Instances.Single(instance => instance.Key == recorded[0]);
        bool renewed = await gateway.RenewAsync(renewing);
        release.SetResult();
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await run;

        // Assert: the plan updated api; the instance that renewed stays, the other is dropped.
        recorded.Length.ShouldBe(2);
        renewed.ShouldBeTrue();
        _infrastructure.RunningSettings["api"]["Db:Endpoint"].ShouldBe(MovedEndpoint);
        InstanceKey[] keys = [.. api.Instances.Select(instance => instance.Key)];
        keys.Length.ShouldBe(2);
        keys.ShouldContain(recorded[0]);
        keys.ShouldNotContain(recorded[1]);
    }

    private Task<RunningGateway> StartAsync(IApplicationModel model)
    {
        return RunningGateway.StartAsync(_infrastructure, _store, model);
    }

    private ApplicationGateway CreateGateway(IApplicationModel model)
    {
        return RunningGateway.Create(_infrastructure, _store, model);
    }

    // api reads db's endpoint and a mode given in the model.
    private static IApplicationModel CreateChain(string mode)
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        builder.AddResource("Web", "api")
            .WithSetting("Db:Endpoint", database.Endpoint)
            .WithSetting("Feature:Mode", mode);
        return builder.Build();
    }

    private TestInstance Instance(string resource, Func<TestInstance, bool>? match = null)
    {
        return _infrastructure.Instances.Values
            .Select(entry => entry.Instance)
            .Single(instance => instance.Name.Value == resource && (match is null || match(instance)));
    }

    private static IApplicationGatewayDeployment Resource(ApplicationGateway gateway, string name)
    {
        return gateway.Resources.Single(resource => resource.Manifest.Name.Value == name);
    }

    // Writes the level on the platform's first instance of the resource and records it through the
    // gateway, which restores it on the others.
    private async Task WriteLogLevelAsync(ApplicationGateway gateway, string resource, string level)
    {
        (TestInstance instance, ApplicationInstanceService service) = _infrastructure.Instances.Values.First(entry => entry.Instance.Name.Value == resource);
        ApplicationActionResult write = await service.ControlPlane.ExecuteAsync(
            "log-level",
            ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json, Encoding.UTF8.GetBytes($$"""{"Level":"{{level}}"}""")));

        foreach (InstanceKey target in await gateway.RecordAsync(instance.Provider, instance.Name, instance.Key, write.State!))
        {
            await _infrastructure.Instances[target].Service.ControlPlane.RestoreAsync(write.State!);
        }
    }

    private void ShouldPrecede(string first, string second)
    {
        string[] events = _infrastructure.Events();
        int earlier = Array.IndexOf(events, first);
        Array.IndexOf(events, second).ShouldBeGreaterThan(earlier, $"'{second}' should follow '{first}' in: {string.Join(", ", events)}");
        earlier.ShouldBeGreaterThanOrEqualTo(0, $"'{first}' is missing from: {string.Join(", ", events)}");
    }
}
