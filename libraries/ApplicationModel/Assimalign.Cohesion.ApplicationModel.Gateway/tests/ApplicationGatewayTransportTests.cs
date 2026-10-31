using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class ApplicationGatewayTransportTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - Gateway transports: ";

    [Fact(DisplayName = DisplayPrefix + "A transport starts before anything deploys, carries the instances' publications, and stops with the run")]
    public async Task RunAsync_WithTransport_ShouldStartItFirstAndStopItWhenTheRunEnds()
    {
        // Arrange
        var infrastructure = new TestInfrastructure();
        var transport = new TestTransport(infrastructure);
        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.UseInfrastructure(infrastructure).UseTransport(transport).Build();
        using var stop = new CancellationTokenSource();

        // Act
        Task run = gateway.RunAsync(stop.Token);
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));
        int stopsWhileRunning = transport.Stops;
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        transport.EventsAtStart.ShouldBeEmpty();
        transport.Starts.ShouldBe(1);
        stopsWhileRunning.ShouldBe(0);
        transport.Stops.ShouldBe(1);
        gateway.Resources.ShouldHaveSingleItem().Instances.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "A transport that cannot start fails the run before anything deploys")]
    public async Task RunAsync_WithTransportThatCannotStart_ShouldFailBeforeDeploying()
    {
        // Arrange
        var infrastructure = new TestInfrastructure();
        var first = new TestTransport(infrastructure);
        var failing = new TestTransport(infrastructure) { StartFailure = new InvalidOperationException("no listener") };
        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("local");
        builder.AddResource("Web", "orders-api");
        ApplicationGateway gateway = builder.UseInfrastructure(infrastructure).UseTransport(first).UseTransport(failing).Build();

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => gateway.RunAsync());

        // Assert
        exception.Message.ShouldBe("no listener");
        infrastructure.Events().ShouldBeEmpty();
        first.Stops.ShouldBe(1);
        failing.Stops.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "A transport is added once")]
    public void UseTransport_WithAddedTransport_ShouldThrow()
    {
        // Arrange
        var transport = new TestTransport(new TestInfrastructure());
        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("local").UseTransport(transport);

        // Act & Assert
        Should.Throw<ArgumentException>(() => builder.UseTransport(transport));
    }
}
