using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Instances renew their registrations with short leases; one that stops renewing, even while the
/// gateway was down, leaves within one lease.
/// </summary>
public sealed class ApplicationGatewayLeaseTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway leases: ";

    // Instances renew three times per lease, so a registration lapses wrongly only if three renewals
    // in a row are lost to a busy machine.
    private static readonly TimeSpan _lease = TimeSpan.FromMilliseconds(600);

    private readonly TemporaryDirectory _directory = new();
    private readonly TestInfrastructure _infrastructure = new();
    private readonly JsonFileGatewayStore _store;

    public ApplicationGatewayLeaseTests()
    {
        _store = JsonFileGatewayStore.Create(_directory.FullPath);
    }

    public void Dispose()
    {
        foreach ((_, ResourceService service) in _infrastructure.Instances.Values)
        {
            service.StopAsync().GetAwaiter().GetResult();
        }

        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "An instance that stops renewing is unregistered once its lease lapses")]
    public async Task Expiry_WhenInstanceStopsRenewing_ShouldUnregisterIt()
    {
        // Arrange
        await using RunningGateway running = await StartAsync(_store);
        ResourceService service = _infrastructure.Instances.Values.Single().Service;

        // Act: the instance dies, so its renewals stop.
        await service.StopAsync();
        await Eventually.WaitUntilAsync(() => running.Gateway.Resources.Single().Instances.Count == 0);

        // Assert
        await Eventually.WaitUntilAsync(() => _store.LoadAsync("root").AsTask().Result!.Resources.Single().Instances.Count == 0);
    }

    [Fact(DisplayName = DisplayPrefix + "A running instance stays registered across many leases")]
    public async Task Renewal_WhileInstanceRuns_ShouldKeepItRegistered()
    {
        // Arrange
        await using RunningGateway running = await StartAsync(_store);

        // Act
        await Task.Delay(_lease * 3);

        // Assert
        running.Gateway.Resources.Single().Instances.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "A recorded instance that died while the gateway was down leaves within one lease")]
    public async Task Expiry_AfterRestart_ShouldDropRecordedInstancesThatNoLongerRenew()
    {
        // Arrange: the instance dies along with the first gateway.
        await using (await StartAsync(_store))
        {
            await _infrastructure.Instances.Values.Single().Service.StopAsync();
        }

        // Act
        await using RunningGateway second = await StartAsync(_store);
        int adopted = second.Gateway.Resources.Single().Instances.Count;
        await Eventually.WaitUntilAsync(() => second.Gateway.Resources.Single().Instances.Count == 0);

        // Assert
        adopted.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "A running instance a restarted gateway does not know publishes again")]
    public async Task Renewal_WhenRestartedGatewayHasNoRecord_ShouldRegisterTheInstanceAgain()
    {
        // Arrange
        await using (await StartAsync(_store))
        {
        }

        // Act: the second gateway starts without the first one's record.
        using var otherDirectory = new TemporaryDirectory();
        await using RunningGateway second = await StartAsync(JsonFileGatewayStore.Create(otherDirectory.FullPath));
        int adopted = second.Gateway.Resources.Single().Instances.Count;
        await Eventually.WaitUntilAsync(() => second.Gateway.Resources.Single().Instances.Count == 1);

        // Assert
        adopted.ShouldBe(0);
        _infrastructure.Deployed.ShouldHaveSingleItem();
    }

    private Task<RunningGateway> StartAsync(IGatewayStore store)
    {
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        builder.AddResource("Web", "orders-api");
        return RunningGateway.StartAsync(_infrastructure, store, builder.Build().Model, leaseDuration: _lease);
    }
}
