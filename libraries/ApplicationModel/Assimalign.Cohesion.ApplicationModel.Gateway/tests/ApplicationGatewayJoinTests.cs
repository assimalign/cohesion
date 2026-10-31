using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Child gateways joining their parent: each deploys on its own, registers with the parent under a
/// lease, and fills in its settings from the parent's outputs.
/// </summary>
public sealed class ApplicationGatewayJoinTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway joins: ";

    private const string IdentityEndpoint = "http://identity.root.svc";

    private const string MovedIdentityEndpoint = "http://identity-moved.root.svc";

    private static readonly TimeSpan _shortWindow = TimeSpan.FromMilliseconds(400);

    private readonly TemporaryDirectory _directory = new();
    private readonly TestInfrastructure _root = new();
    private readonly TestInfrastructure _east = new();
    private readonly JsonFileApplicationGatewayStore _store;

    public ApplicationGatewayJoinTests()
    {
        _store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "A child fills in its settings from its parent's outputs and its own")]
    public async Task RunAsync_WithParent_ShouldFillInSettingsFromTheParentsOutputs()
    {
        // Arrange
        await using RunningGateway root = await StartRootAsync();

        // Act
        await using RunningGateway east = await StartEastAsync(root.Gateway);

        // Assert: each gateway deployed only its own resources.
        TestInstance api = Instance("orders-api");
        api.Settings["Identity:Authority"].ShouldBe(IdentityEndpoint);
        api.Settings["Database:Endpoint"].ShouldBe("http://orders-db.east.svc");
        east.Gateway.Model.Role.ShouldBe(ApplicationGatewayRole.Composite);
        root.Gateway.Children.ShouldHaveSingleItem().Name.ShouldBe(new GatewayName("east"));
        _root.Deployed.ShouldBe(["root:IdentityHub/identity"]);
        _east.Deployed.ShouldBe(["east:Database.Sql/orders-db", "east:Web/orders-api"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A child that joins before its parent's output is ready deploys nothing until it is")]
    public async Task RunAsync_BeforeParentOutputIsReady_ShouldWaitForIt()
    {
        // Arrange: the root's identity provider starts only when the test lets it.
        var release = new TaskCompletionSource();
        _root.Delayed["identity"] = () => release.Task;
        ApplicationGateway root = RunningGateway.Create(_root, _store, CreateRoot());
        ApplicationGateway east = RunningGateway.Create(_east, _store, CreateEast(), parent: root);
        using var stop = new CancellationTokenSource();
        Task rootRun = root.RunAsync(stop.Token);
        Task eastRun = east.RunAsync(stop.Token);

        // Act
        await Eventually.WaitUntilAsync(() => root.Children.Count == 1);
        string[] beforeRelease = _east.Events();
        release.SetResult();
        await east.Started.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await Task.WhenAll(rootRun, eastRun);

        // Assert
        beforeRelease.ShouldBeEmpty();
        Instance("orders-api").Settings["Identity:Authority"].ShouldBe(IdentityEndpoint);
    }

    [Fact(DisplayName = DisplayPrefix + "A parent that never reports an output the child uses fails the child's run before it deploys")]
    public async Task RunAsync_WhenParentNeverReportsTheOutput_ShouldFailWithOutputMissing()
    {
        // Arrange
        _root.Outputs["identity"] = new Dictionary<OutputName, string>();
        await using RunningGateway root = await StartRootAsync();
        ApplicationGateway east = RunningGateway.Create(_east, _store, CreateEast(), _shortWindow, parent: root.Gateway);

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => east.RunAsync());

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.OutputMissing);
        exception.Message.ShouldContain(
            "Setting 'Identity:Authority' of resource 'Web/orders-api' in gateway 'east' uses output 'endpoint' of 'IdentityHub/identity' in parent gateway 'root'",
            Case.Sensitive);
        _east.Events().ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A parent that is not running fails the child's run before it deploys")]
    public async Task RunAsync_WhenParentIsNotRunning_ShouldFailWithStartupTimedOut()
    {
        // Arrange: the root gateway exists but never runs.
        ApplicationGateway root = RunningGateway.Create(_root, _store, CreateRoot());
        ApplicationGateway east = RunningGateway.Create(_east, _store, CreateEast(), _shortWindow, parent: root);

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => east.RunAsync());

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.StartupTimedOut);
        exception.Message.ShouldContain("Gateway 'east' could not join parent 'root'", Case.Sensitive);
        _east.Events().ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A parent refuses a manifest that names another gateway, or none, as its parent")]
    public async Task JoinAsync_WithManifestNamingAnotherParent_ShouldThrow()
    {
        // Arrange
        await using RunningGateway root = await StartRootAsync();
        var elsewhere = new ApplicationManifest("west", "elsewhere", []);
        var orphan = new ApplicationManifest("west", null, []);

        // Act & Assert
        Should.Throw<ArgumentException>(() => { _ = root.Gateway.JoinAsync(elsewhere); }).Message.ShouldContain("names 'elsewhere' as its parent, not 'root'", Case.Sensitive);
        Should.Throw<ArgumentException>(() => { _ = root.Gateway.JoinAsync(orphan); });
        root.Gateway.Children.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A child stays registered while it runs and is dropped once its lease lapses")]
    public async Task JoinAsync_WhenChildStopsRenewing_ShouldDropItAfterItsLease()
    {
        // Arrange: the root keeps a child registered for 300 milliseconds without a join.
        await using RunningGateway root = await RunningGateway.StartAsync(_root, _store, CreateRoot(), leaseDuration: TimeSpan.FromMilliseconds(300));
        int whileRunning;

        // Act: east renews three times per lease while it runs, then stops.
        await using (await StartEastAsync(root.Gateway))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900));
            whileRunning = root.Gateway.Children.Count;
        }

        // Assert
        whileRunning.ShouldBe(1);
        await Eventually.WaitUntilAsync(() => root.Gateway.Children.Count == 0);
    }

    [Fact(DisplayName = DisplayPrefix + "A parent's changed output reaches the child on the child's next run, which updates what uses it")]
    public async Task RunAsync_WhenParentOutputChanges_ShouldApplyItOnTheChildsNextRun()
    {
        // Arrange: east runs once with the identity provider's first address; then the platform
        // moves the identity provider, and the root runs again.
        await using (RunningGateway first = await StartRootAsync())
        {
            await using (await StartEastAsync(first.Gateway))
            {
            }
        }

        _root.Outputs["identity"] = new Dictionary<OutputName, string> { [OutputName.Endpoint] = MovedIdentityEndpoint };
        await using RunningGateway root = await StartRootAsync();

        // Act
        await using RunningGateway east = await StartEastAsync(root.Gateway);

        // Assert: orders-api was updated with the new address; orders-db kept running.
        _east.Deployed.ShouldBe(["east:Database.Sql/orders-db", "east:Web/orders-api", "east:Web/orders-api"]);
        Instance("orders-api", instance => instance.Settings["Identity:Authority"] == MovedIdentityEndpoint).ShouldNotBeNull();
    }

    private Task<RunningGateway> StartRootAsync()
    {
        return RunningGateway.StartAsync(_root, _store, CreateRoot());
    }

    private Task<RunningGateway> StartEastAsync(IApplicationGatewayParent parent)
    {
        return RunningGateway.StartAsync(_east, _store, CreateEast(), parent: parent);
    }

    // The root runs the identity provider.
    private static IApplicationModel CreateRoot()
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("IdentityHub", "identity");
        return builder.Build();
    }

    // East joins the root; its orders-api reads the root's identity provider endpoint and that of
    // east's own database.
    private static IApplicationModel CreateEast()
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("east");
        IApplicationParent root = builder.UseParent("root");
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "orders-db");
        builder.AddResource("Web", "orders-api")
            .WithSetting("Identity:Authority", root.Endpoint("IdentityHub", "identity"))
            .WithSetting("Database:Endpoint", database.Endpoint);
        return builder.Build();
    }

    private TestInstance Instance(string resource, Func<TestInstance, bool>? match = null)
    {
        return _east.Instances.Values
            .Select(entry => entry.Instance)
            .Single(instance => instance.Name.Value == resource && (match is null || match(instance)));
    }
}
