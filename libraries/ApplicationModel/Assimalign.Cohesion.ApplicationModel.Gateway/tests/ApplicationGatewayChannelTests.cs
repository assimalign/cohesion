using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// The infrastructure provider's channel, the way the platform's instances reach the gateway: it
/// starts before anything deploys and stops when the run ends.
/// </summary>
public class ApplicationGatewayChannelTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - Gateway channel: ";

    [Fact(DisplayName = DisplayPrefix + "The provider's channel starts before anything deploys, carries the instances' publications, and stops with the run")]
    public async Task StartAsync_WithProvider_ShouldStartItsChannelFirstAndStopItWhenTheRunEnds()
    {
        // Arrange
        var infrastructure = new TestInfrastructure();
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.UseInfrastructure(infrastructure).Build();

        // Act
        await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        int stopsWhileRunning = infrastructure.Stops;
        await gateway.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        infrastructure.EventsAtStart.ShouldBeEmpty();
        infrastructure.Starts.ShouldBe(1);
        stopsWhileRunning.ShouldBe(0);
        infrastructure.Stops.ShouldBe(1);
        gateway.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        gateway.Resources.ShouldHaveSingleItem().Instances.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "A channel that cannot start fails the run before anything deploys and is not stopped")]
    public async Task StartAsync_WithChannelThatCannotStart_ShouldFailBeforeDeploying()
    {
        // Arrange
        var infrastructure = new TestInfrastructure { StartFailure = new InvalidOperationException("no listener") };
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.UseInfrastructure(infrastructure).Build();

        // Act
        InvalidOperationException started = await Should.ThrowAsync<InvalidOperationException>(() => gateway.StartAsync());
        InvalidOperationException completion = await Should.ThrowAsync<InvalidOperationException>(() => gateway.Completion);

        // Assert
        started.Message.ShouldBe("no listener");
        completion.ShouldBeSameAs(started);
        infrastructure.Events().ShouldBeEmpty();
        infrastructure.Stops.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "A channel that fails to stop faults the completion of a run that stopped cleanly")]
    public async Task StopAsync_WithChannelThatCannotStop_ShouldFaultCompletion()
    {
        // Arrange
        var infrastructure = new TestInfrastructure { StopFailure = new InvalidOperationException("still listening") };
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.UseInfrastructure(infrastructure).Build();
        await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        await gateway.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: stopping does not throw; the completion reports the failure.
        (await Should.ThrowAsync<InvalidOperationException>(() => gateway.Completion)).Message.ShouldBe("still listening");
        infrastructure.Stops.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "The context the channel receives carries the model and the gateway as its publisher")]
    public async Task StartAsync_WithProvider_ShouldHandItTheGatewaysContext()
    {
        // Arrange
        var infrastructure = new TestInfrastructure();
        ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.UseInfrastructure(infrastructure).Build();

        // Act
        await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        IApplicationInstancePublisher? publisher = infrastructure.Gateway;
        await gateway.StopAsync(CancellationToken.None);

        // Assert
        gateway.Context.Model.ShouldBeSameAs(gateway.Model);
        gateway.Context.Publisher.ShouldBeSameAs(gateway);
        publisher.ShouldBeSameAs(gateway);
    }
}
