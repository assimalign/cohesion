using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class JsonFileApplicationGatewayStoreTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - JsonFileApplicationGatewayStore: ";

    private readonly TemporaryDirectory _directory = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway that never saved has no record")]
    public async Task LoadAsync_WithoutFile_ShouldReturnNull()
    {
        // Arrange
        JsonFileApplicationGatewayStore store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);

        // Act
        ApplicationGatewayRecord? record = await store.LoadAsync("root");

        // Assert
        record.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "A saved record loads back, snapshot values in any format included")]
    public async Task SaveAsync_ThenLoadAsync_ShouldRoundTrip()
    {
        // Arrange
        JsonFileApplicationGatewayStore store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);
        var instance = new ApplicationInstanceManifest("Web", "orders-api", InstanceKey.New(), DeploymentId.New(), new Uri("http://10.0.0.5:9090/"), [new ApplicationActionDescriptor("cors", ApplicationActionKind.ReadWrite, IsStateful: true)]);
        byte[] binary = [0xFF, 0x00, 0x7B];
        var outputs = new Dictionary<OutputName, string> { [OutputName.Endpoint] = "http://orders-api.root.svc" };
        var saved = new ApplicationGatewayRecord(
            "root",
            [new ApplicationResourceRecord("Web", "orders-api", "abc:1", [ApplicationActionSnapshot.Create("cors", binary)], [instance], outputs)])
        {
            Epoch = 3,
        };

        // Act
        bool stored = await store.SaveAsync(saved);
        ApplicationGatewayRecord? loaded = await store.LoadAsync("root");

        // Assert
        stored.ShouldBeTrue();
        loaded.ShouldNotBeNull();
        loaded.Gateway.ShouldBe(saved.Gateway);
        loaded.Epoch.ShouldBe(3);
        ApplicationResourceRecord resource = loaded.Resources.ShouldHaveSingleItem();
        resource.Revision.ShouldBe("abc:1");
        resource.State.ShouldHaveSingleItem().Value.ToArray().ShouldBe(binary);
        resource.Instances.ShouldHaveSingleItem().ShouldBe(instance with { Actions = resource.Instances[0].Actions });
        resource.Instances[0].Actions.ShouldBe(instance.Actions);
        resource.Outputs.ShouldBe(outputs);
    }

    [Fact(DisplayName = DisplayPrefix + "A save replaces the previous record and leaves no temporary file")]
    public async Task SaveAsync_Twice_ShouldReplaceTheRecord()
    {
        // Arrange
        JsonFileApplicationGatewayStore store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);

        // Act
        await store.SaveAsync(new ApplicationGatewayRecord("root", [new ApplicationResourceRecord("Web", "orders-api", "abc:1", [], [], new Dictionary<OutputName, string>())]));
        await store.SaveAsync(new ApplicationGatewayRecord("root", []));

        // Assert
        (await store.LoadAsync("root")).ShouldNotBeNull().Resources.ShouldBeEmpty();
        Directory.GetFiles(store.DirectoryPath, "*.json").ShouldBe([Path.Combine(store.DirectoryPath, "root.json")]);
        Directory.GetFiles(store.DirectoryPath, "*.tmp").ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "Each run takes the record over with a higher epoch, and loading takes nothing over")]
    public async Task AcquireAsync_EachRun_ShouldTakeAHigherEpoch()
    {
        // Arrange
        JsonFileApplicationGatewayStore store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);
        var resource = new ApplicationResourceRecord("Web", "orders-api", "abc:1", [], [], new Dictionary<OutputName, string>());

        // Act
        ApplicationGatewayRecord first = await store.AcquireAsync("root");
        await store.SaveAsync(first with { Resources = [resource] });
        ApplicationGatewayRecord? loaded = await store.LoadAsync("root");
        ApplicationGatewayRecord second = await store.AcquireAsync("root");

        // Assert
        first.Epoch.ShouldBe(1);
        first.Resources.ShouldBeEmpty();
        loaded.ShouldNotBeNull().Epoch.ShouldBe(1);
        second.Epoch.ShouldBe(2);
        second.Resources.ShouldHaveSingleItem().Name.Value.ShouldBe("orders-api");
    }

    [Fact(DisplayName = DisplayPrefix + "A save from a run that a later run took the record over from is refused")]
    public async Task SaveAsync_FromAnEarlierRun_ShouldBeRefused()
    {
        // Arrange
        JsonFileApplicationGatewayStore store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);
        var resource = new ApplicationResourceRecord("Web", "orders-api", "abc:1", [], [], new Dictionary<OutputName, string>());
        ApplicationGatewayRecord earlier = await store.AcquireAsync("root");
        ApplicationGatewayRecord later = await store.AcquireAsync("root");

        // Act
        bool earlierSaved = await store.SaveAsync(earlier with { Resources = [resource] });
        ApplicationGatewayRecord? afterRefusal = await store.LoadAsync("root");
        bool laterSaved = await store.SaveAsync(later with { Resources = [resource] });

        // Assert
        earlierSaved.ShouldBeFalse();
        afterRefusal.ShouldNotBeNull().Epoch.ShouldBe(2);
        afterRefusal.Resources.ShouldBeEmpty();
        laterSaved.ShouldBeTrue();
        (await store.LoadAsync("root")).ShouldNotBeNull().Resources.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = DisplayPrefix + "Runs that take one record over at the same time, through separate stores, each get an epoch of their own")]
    public async Task AcquireAsync_FromManyStoresAtOnce_ShouldGiveEachRunItsOwnEpoch()
    {
        // Arrange: eight stores over one directory, as eight gateway processes would open it.
        JsonFileApplicationGatewayStore[] stores = [.. Enumerable.Range(0, 8).Select(_ => JsonFileApplicationGatewayStore.Create(_directory.FullPath))];

        // Act
        long[] epochs = await Task.WhenAll(stores.Select(store => Task.Run(async () => (await store.AcquireAsync("root")).Epoch)));

        // Assert
        epochs.Order().ShouldBe([1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L]);
        (await stores[0].LoadAsync("root")).ShouldNotBeNull().Epoch.ShouldBe(8);
    }

    [Theory(DisplayName = DisplayPrefix + "A store needs a directory path")]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutDirectory_ShouldThrow(string directoryPath)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => JsonFileApplicationGatewayStore.Create(directoryPath));
    }
}
