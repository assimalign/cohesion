using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

public sealed class DatabaseResourceLifecycleTests
{
    private const string displayPrefix = "Cohesion Test [Database.Hosting.ApplicationModel] - Lifecycle: ";

    [Fact(DisplayName = displayPrefix + "Publication and restore precede SQL accept; the server drains before the client stops")]
    public async Task StartAsync_WithSnapshot_ShouldPublishAndRestoreBeforeListeningAndCloseAfterDrain()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var action = new GreetingAction();
        var client = new TestGatewayClient
        {
            Snapshots = [ApplicationActionSnapshot.Create("greeting", Encoding.UTF8.GetBytes("""{"text":"restored"}"""))],
        };
        await using RunningResource resource = RunningResource.Build(client, options => options
            .AddAction(action, GreetingJsonContext.Default)
            .AddAction(new GreetingAction(name: "second"), GreetingJsonContext.Default));
        int publications = 0;
        int openings = 0;
        int closings = 0;
        client.OnPublish = async token =>
        {
            (await TestPorts.AcceptsAsync(resource.Port, token)).ShouldBeFalse();
            var engine = resource.Application.Context.GetEngine<SqlDatabaseEngine>("orders-sql");
            engine.TryGetDatabase("orders", out SqlDatabase? database).ShouldBeTrue();
            await using var session = await database!.CreateSessionAsync(cancellationToken: token);
            await session.ExecuteAsync("INSERT INTO orders VALUES (1)", cancellationToken: token);
            publications++;
        };
        client.OnStart = async token =>
        {
            action.Value.Text.ShouldBe("restored");
            (await TestPorts.AcceptsAsync(resource.Port, token)).ShouldBeFalse();
            openings++;
        };
        client.OnStop = async token =>
        {
            (await TestPorts.AcceptsAsync(resource.Port, token)).ShouldBeFalse();
            closings++;
        };

        // Act
        await resource.StartAsync(guard.Token);

        // Assert: callbacks record the boundaries; no elapsed-time observation decides ordering.
        publications.ShouldBe(1);
        openings.ShouldBe(1);
        client.Started.ShouldBeTrue();
        client.Serving.ShouldNotBeNull();
        (await TestPorts.AcceptsAsync(resource.Port, guard.Token)).ShouldBeTrue();
        ApplicationInstanceManifest manifest = client.Manifests.ShouldHaveSingleItem();
        manifest.Provider.ShouldBe((ProviderName)"Database");
        manifest.Name.ShouldBe((ResourceName)"orders-db");
        manifest.Actions.Select(static item => item.Name).ShouldBe(new ActionName[] { "greeting", "second" });

        await resource.StopAsync(guard.Token);
        closings.ShouldBe(1);
        client.Stopped.ShouldBeTrue();
        client.Serving.ShouldBeNull();
        resource.Application.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = displayPrefix + "An unmanaged application serves without a resource service")]
    public async Task StartAsync_WithoutPlatform_ShouldServeUnmanaged()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using RunningResource resource = RunningResource.Build(gateway: null);

        // Act
        await resource.StartAsync(guard.Token);

        // Assert
        (await TestPorts.AcceptsAsync(resource.Port, guard.Token)).ShouldBeTrue();
        resource.Application.Context.HostedServices.OfType<Assimalign.Cohesion.ApplicationModel.Hosting.IApplicationInstanceService>().ShouldBeEmpty();
        await resource.StopAsync(guard.Token);
    }
}
