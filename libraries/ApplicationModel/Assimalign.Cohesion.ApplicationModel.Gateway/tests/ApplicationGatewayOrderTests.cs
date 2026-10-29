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
/// A gateway deploying in dependency order: each resource or child gateway once what it depends on
/// is ready, independent ones at the same time.
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
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition web = builder.AddResource("Web", "web");
        IApplicationResourceDefinition api = builder.AddResource("Web", "api");
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        IApplicationResourceDefinition cache = builder.AddResource("Caching", "cache");
        web.DependsOn(api);
        api.DependsOn(database, "data.read").DependsOn(cache);

        // Act
        await using RunningGateway running = await StartAsync(builder.Build().Model);

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
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        builder.AddResource("Caching", "cache");
        builder.AddResource("Web", "api").DependsOn(database);
        var release = new TaskCompletionSource();
        _infrastructure.Delayed["db"] = () => release.Task;
        ApplicationGateway gateway = CreateGateway(TimeSpan.FromSeconds(10));
        using var stop = new CancellationTokenSource();

        // Act
        Task run = gateway.RunAsync(builder.Build().Model, stop.Token);
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
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        builder.AddResource("Caching", "cache");
        IApplicationResourceDefinition api = builder.AddResource("Web", "api").DependsOn(database);
        builder.AddResource("Web", "web").DependsOn(api);
        _infrastructure.Silent.Add("db");
        ApplicationGateway gateway = CreateGateway(_shortWindow);

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.RunAsync(builder.Build().Model));

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
        await Should.ThrowAsync<ApplicationGatewayException>(() => CreateGateway(_shortWindow).RunAsync(model));

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
        ApplicationGateway gateway = CreateGateway(TimeSpan.FromSeconds(1));
        Task run = gateway.RunAsync(model);

        // Act: an instance of the api the platform still runs restarts while api waits for db.
        await Eventually.WaitUntilAsync(() => _infrastructure.Events().Count(call => call == "deploy:db") == 2);
        TestInstance restarted = await _infrastructure.StartInstanceAsync("Web", "api");
        IApplicationResourceDeployment api = gateway.Resources.Single(resource => resource.Manifest.Name.Value == "api");
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

    [Fact(DisplayName = DisplayPrefix + "A child gateway deploys once the gateway's resources its part depends on are ready")]
    public async Task RunAsync_WithChildGatewayDependingOnParent_ShouldDeployItAfterThem()
    {
        // Arrange: orders-api, two levels below the root, uses the root's identity provider, which
        // starts after a delay; west depends on nothing.
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition identity = builder.AddResource("IdentityHub", "identity");
        builder.AddGateway("east").AddGateway("east-edge").AddResource("Web", "orders-api").DependsOn(identity, "tokens.issue");
        builder.AddGateway("west");
        _infrastructure.Delayed["identity"] = () => Task.Delay(200);

        // Act
        await using RunningGateway running = await StartAsync(builder.Build().Model);

        // Assert
        ShouldPrecede("outputs:identity", "gateway:east");
        ShouldPrecede("gateway:west", "outputs:identity");
        _infrastructure.DeployedGateways.Select(name => name.Value).ShouldBe(["west", "east"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A resource that depends on a child gateway's resource deploys once that child gateway has started")]
    public async Task RunAsync_WithResourceDependingOnChild_ShouldDeployItAfterTheChildGateway()
    {
        // Arrange
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition ordersApi = builder.AddGateway("east").AddResource("Web", "orders-api");
        builder.AddResource("Web", "app-gateway").DependsOn(ordersApi);

        // Act
        await using RunningGateway running = await StartAsync(builder.Build().Model);

        // Assert
        ShouldPrecede("gateway:east", "deploy:app-gateway");
    }

    [Fact(DisplayName = DisplayPrefix + "A part with a child gateway that is both waited for and waiting is refused before anything deploys")]
    public async Task RunAsync_WhenChildGatewayCannotBeOrdered_ShouldRefuseBeforeDeploying()
    {
        // Arrange: the root's app-gateway routes to east's orders-api, and east's orders-worker calls
        // through app-gateway, so east would deploy both before and after app-gateway.
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationSet east = builder.AddGateway("east");
        IApplicationResourceDefinition ordersApi = east.AddResource("Web", "orders-api");
        IApplicationResourceDefinition appGateway = builder.AddResource("Web", "app-gateway").DependsOn(ordersApi);
        east.AddResource("Web", "orders-worker").DependsOn(appGateway);
        ApplicationGateway gateway = CreateGateway(_shortWindow);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(() => gateway.RunAsync(builder.Build().Model));

        // Assert
        exception.Message.ShouldContain("Gateway 'root' cannot deploy its part in dependency order", Case.Sensitive);
        exception.Message.ShouldContain("Web/app-gateway waits for gateway 'east', because root/Web/app-gateway depends on east/Web/orders-api", Case.Sensitive);
        exception.Message.ShouldContain("gateway 'east' waits for Web/app-gateway, because east/Web/orders-worker depends on root/Web/app-gateway", Case.Sensitive);
        _infrastructure.Events().ShouldBeEmpty();
        await Should.ThrowAsync<ArgumentException>(() => gateway.Started);
    }

    [Fact(DisplayName = DisplayPrefix + "A part is refused when a gateway below it cannot deploy its own part in order")]
    public async Task RunAsync_WhenDescendantCannotBeOrdered_ShouldRefuseBeforeDeploying()
    {
        // Arrange: inside east, ingress depends on east-edge's cache, and east-edge's edge-api depends
        // on ingress.
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationSet east = builder.AddGateway("east");
        IApplicationSet edge = east.AddGateway("east-edge");
        IApplicationResourceDefinition ingress = east.AddResource("Web", "ingress").DependsOn(edge.AddResource("Caching", "cache"));
        edge.AddResource("Web", "edge-api").DependsOn(ingress);
        ApplicationGateway gateway = CreateGateway(_shortWindow);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(() => gateway.RunAsync(builder.Build().Model));

        // Assert
        exception.Message.ShouldContain("Gateway 'east' cannot deploy its part in dependency order", Case.Sensitive);
        _infrastructure.Events().ShouldBeEmpty();
    }

    private Task<RunningGateway> StartAsync(IApplicationModel model, TimeSpan? startupTimeout = null)
    {
        return RunningGateway.StartAsync(_infrastructure, _store, model, startupTimeout);
    }

    private ApplicationGateway CreateGateway(TimeSpan startupTimeout)
    {
        ApplicationGateway gateway = ApplicationGateway.Create(_infrastructure, _store, startupTimeout);
        _infrastructure.Gateway = gateway;
        return gateway;
    }

    // api depends on db.
    private static IApplicationModel CreateChain()
    {
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        builder.AddResource("Web", "api").DependsOn(database, "data.read");
        return builder.Build().Model;
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
