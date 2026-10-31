using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A gateway deploying in dependency order: each resource once what it depends on is ready,
/// independent ones at the same time.
/// </summary>
public sealed class ApplicationGatewayOrderTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway order: ";

    private static readonly TimeSpan _shortWindow = TimeSpan.FromMilliseconds(300);

    private readonly TemporaryDirectory _directory = new();
    private readonly TestInfrastructure _infrastructure = new();
    private readonly JsonFileApplicationGatewayStore _store;

    public ApplicationGatewayOrderTests()
    {
        _store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "A resource deploys only once what it depends on has published and its outputs are read")]
    public async Task RunAsync_WithDependencies_ShouldDeployEachResourceAfterItsDependencies()
    {
        // Arrange: web depends on api, which depends on db and cache.
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition web = builder.AddResource("Web", "web");
        IApplicationResourceDefinition api = builder.AddResource("Web", "api");
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        IApplicationResourceDefinition cache = builder.AddResource("Caching", "cache");
        web.DependsOn(api);
        api.DependsOn(database, "data.read").DependsOn(cache);

        // Act
        await using RunningGateway running = await StartAsync(builder.Build());

        // Assert
        ShouldPrecede("outputs:db", "deploy:api");
        ShouldPrecede("outputs:cache", "deploy:api");
        ShouldPrecede("outputs:api", "deploy:web");
        running.Gateway.Resources.Select(resource => resource.Manifest.Name.Value).ShouldBe(["db", "cache", "api", "web"]);
    }

    [Fact(DisplayName = DisplayPrefix + "Resources that do not depend on each other deploy without waiting for each other")]
    public async Task RunAsync_WithIndependentResources_ShouldNotWaitForEachOther()
    {
        // Arrange: api waits for db, whose instance starts only when the test lets it; cache waits
        // for nothing.
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        builder.AddResource("Caching", "cache");
        builder.AddResource("Web", "api").DependsOn(database);
        var release = new TaskCompletionSource();
        _infrastructure.Delayed["db"] = () => release.Task;
        ApplicationGateway gateway = CreateGateway(builder.Build(), TimeSpan.FromSeconds(10));
        using var stop = new CancellationTokenSource();

        // Act
        Task run = gateway.RunAsync(stop.Token);
        await Eventually.WaitUntilAsync(() => _infrastructure.IndexOf("outputs:cache") >= 0);
        string[] beforeRelease = _infrastructure.Events();
        release.SetResult();
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await run;

        // Assert
        beforeRelease.ShouldContain("deploy:db");
        beforeRelease.ShouldNotContain("deploy:api");
        ShouldPrecede("outputs:db", "deploy:api");
    }

    [Fact(DisplayName = DisplayPrefix + "What depends on a resource that misses its window is not deployed, and the run names both")]
    public async Task RunAsync_WhenDependencyMissesItsWindow_ShouldSkipWhatDependsOnIt()
    {
        // Arrange: db never starts; api depends on db and web on api; cache depends on nothing.
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        builder.AddResource("Caching", "cache");
        IApplicationResourceDefinition api = builder.AddResource("Web", "api").DependsOn(database);
        builder.AddResource("Web", "web").DependsOn(api);
        _infrastructure.Silent.Add("db");
        ApplicationGateway gateway = CreateGateway(builder.Build(), _shortWindow);

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.RunAsync());

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.StartupTimedOut);
        exception.Message.ShouldContain("within 00:00:00.3000000: Database.Sql/db.", Case.Sensitive);
        exception.Message.ShouldContain("did not start: Web/api, Web/web.", Case.Sensitive);
        _infrastructure.Deployed.ShouldBe(["root:Database.Sql/db", "root:Caching/cache"]);
        _infrastructure.TornDown.Select(name => name.Value).ShouldBe(["db"]);
        ApplicationGatewayRecord record = (await _store.LoadAsync("root")).ShouldNotBeNull();
        record.Resources.Select(resource => resource.Name.Value).ShouldBe(["cache"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A resource the run did not deploy keeps the record entry it had before the run")]
    public async Task RunAsync_WhenDependencyMisses_ShouldKeepWhatItSkippedRecordedAsBefore()
    {
        // Arrange: the first run deploys db and api, and records a write on api.
        IApplicationModel model = CreateChain();
        await using (RunningGateway first = await StartAsync(model))
        {
            await WriteLogLevelAsync(first.Gateway, "api", "Debug");
        }

        ApplicationResourceRecord before = (await _store.LoadAsync("root"))!.Resources.Single(resource => resource.Name.Value == "api");

        // Act: both are redefined, and db's new revision never starts.
        _infrastructure.Revisions["db"] = "2";
        _infrastructure.Revisions["api"] = "2";
        _infrastructure.Silent.Add("db");
        await Should.ThrowAsync<ApplicationGatewayException>(() => CreateGateway(model, _shortWindow).RunAsync());

        // Assert
        ApplicationResourceRecord after = (await _store.LoadAsync("root"))!.Resources.Single(resource => resource.Name.Value == "api");
        after.Revision.ShouldBe(before.Revision);
        after.Instances.ShouldHaveSingleItem().Key.ShouldBe(before.Instances.Single().Key);
        Encoding.UTF8.GetString(after.State.ShouldHaveSingleItem().Value.Span).ShouldBe("""{"Level":"Debug"}""");
        _infrastructure.Running["api"].ShouldBe("1");
    }

    [Fact(DisplayName = DisplayPrefix + "An instance that publishes before its resource deploys does not make the resource ready")]
    public async Task PublishAsync_BeforeDeploymentStarts_ShouldNotMakeTheResourceReady()
    {
        // Arrange: after a first run, both are redefined; db's new instance starts only when the test
        // lets it, and api's new instance never starts.
        IApplicationModel model = CreateChain();
        await using (await StartAsync(model))
        {
        }

        _infrastructure.Revisions["db"] = "2";
        _infrastructure.Revisions["api"] = "2";
        var release = new TaskCompletionSource();
        _infrastructure.Delayed["db"] = () => release.Task;
        _infrastructure.Silent.Add("api");
        ApplicationGateway gateway = CreateGateway(model, TimeSpan.FromSeconds(1));
        Task run = gateway.RunAsync();

        // Act: an instance of the api the platform still runs restarts while api waits for db.
        await Eventually.WaitUntilAsync(() => _infrastructure.Events().Count(call => call == "deploy:db") == 2);
        TestInstance restarted = await _infrastructure.StartInstanceAsync("Web", "api");
        IApplicationGatewayDeployment api = gateway.Resources.Single(resource => resource.Manifest.Name.Value == "api");
        bool registered = api.Instances.Any(instance => instance.Key == restarted.Key);
        release.SetResult();

        // Assert: the restarted instance registered, but only an instance of the deployment counts.
        registered.ShouldBeTrue();
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => run);
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.StartupTimedOut);
        exception.Message.ShouldContain(": Web/api.", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "Each resource's startup window starts at its own deployment")]
    public async Task RunAsync_WithChain_ShouldGiveEachResourceItsOwnWindow()
    {
        // Arrange: each instance starts 1.2 seconds after its deployment, so the chain takes longer
        // than one window.
        _infrastructure.Delayed["db"] = () => Task.Delay(TimeSpan.FromSeconds(1.2));
        _infrastructure.Delayed["api"] = () => Task.Delay(TimeSpan.FromSeconds(1.2));

        // Act
        await using RunningGateway running = await StartAsync(CreateChain(), TimeSpan.FromSeconds(2));

        // Assert
        running.Gateway.Resources.ShouldAllBe(resource => resource.Instances.Count == 1);
    }

    private Task<RunningGateway> StartAsync(IApplicationModel model, TimeSpan? startupTimeout = null)
    {
        return RunningGateway.StartAsync(_infrastructure, _store, model, startupTimeout);
    }

    private ApplicationGateway CreateGateway(IApplicationModel model, TimeSpan startupTimeout)
    {
        return RunningGateway.Create(_infrastructure, _store, model, startupTimeout);
    }

    // api depends on db.
    private static IApplicationModel CreateChain()
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        builder.AddResource("Web", "api").DependsOn(database, "data.read");
        return builder.Build();
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
