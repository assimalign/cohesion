using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// The values a platform gives deployed resources, such as their endpoints: recorded once startup
/// completes, saved with the record, and resolvable through a descriptor's placeholder.
/// </summary>
public sealed class ApplicationGatewayOutputTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway outputs: ";

    private readonly TemporaryDirectory _directory = new();
    private readonly TestInfrastructure _infrastructure = new();
    private readonly JsonFileGatewayStore _store;

    public ApplicationGatewayOutputTests()
    {
        _store = JsonFileGatewayStore.Create(_directory.FullPath);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "A resource's outputs are recorded and saved once startup completes")]
    public async Task RunAsync_AfterStartup_ShouldRecordEachResourcesOutputs()
    {
        // Arrange
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition store = builder.AddResource("ConfigurationStore", "settings");

        // Act
        await using RunningGateway running = await RunningGateway.StartAsync(_infrastructure, _store, builder.Build().Model);

        // Assert
        running.Gateway.Resources.Single().Outputs[OutputName.Endpoint].ShouldBe("http://settings.root.svc");
        running.Gateway.TryGetOutput(store.Endpoint, out string? endpoint).ShouldBeTrue();
        endpoint.ShouldBe("http://settings.root.svc");
        GatewayRecord record = (await _store.LoadAsync("root")).ShouldNotBeNull();
        record.Resources.Single().Outputs[OutputName.Endpoint].ShouldBe("http://settings.root.svc");
    }

    [Fact(DisplayName = DisplayPrefix + "An output the platform changed is recorded on the next run")]
    public async Task RunAsync_WhenPlatformChangesAnOutput_ShouldRecordTheNewValue()
    {
        // Arrange
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition store = builder.AddResource("ConfigurationStore", "settings");
        IApplicationModel model = builder.Build().Model;
        await using (await RunningGateway.StartAsync(_infrastructure, _store, model))
        {
        }

        // The load balancer in front of the store gets a new address.
        _infrastructure.Outputs["settings"] = new Dictionary<OutputName, string> { [OutputName.Endpoint] = "http://10.0.0.7:8080" };

        // Act
        await using RunningGateway second = await RunningGateway.StartAsync(_infrastructure, _store, model);

        // Assert
        _infrastructure.Deployed.ShouldHaveSingleItem();
        second.Gateway.TryGetOutput(store.Endpoint, out string? endpoint).ShouldBeTrue();
        endpoint.ShouldBe("http://10.0.0.7:8080");
        (await _store.LoadAsync("root")).ShouldNotBeNull().Resources.Single().Outputs[OutputName.Endpoint].ShouldBe("http://10.0.0.7:8080");
    }

    [Fact(DisplayName = DisplayPrefix + "A placeholder resolves only for this gateway's resources and the outputs they have")]
    public async Task TryGetOutput_ShouldResolveOnlyThisGatewaysRecordedOutputs()
    {
        // Arrange: the root runs the identity provider; east runs orders-api.
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition identity = builder.AddResource("IdentityHub", "identity");
        IApplicationResourceDefinition ordersApi = builder.AddGateway("east").AddResource("Web", "orders-api");
        await using RunningGateway root = await RunningGateway.StartAsync(_infrastructure, _store, builder.Build().Model);

        // Act
        bool own = root.Gateway.TryGetOutput(identity.Endpoint, out _);
        bool unknown = root.Gateway.TryGetOutput(identity.Output("admin-endpoint"), out _);
        bool foreign = root.Gateway.TryGetOutput(ordersApi.Endpoint, out _);

        // Assert
        own.ShouldBeTrue();
        unknown.ShouldBeFalse();
        foreign.ShouldBeFalse();
    }
}
