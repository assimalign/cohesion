using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class JsonFileGatewayStoreTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - JsonFileGatewayStore: ";

    private readonly TemporaryDirectory _directory = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway that never saved has no record")]
    public async Task LoadAsync_WithoutFile_ShouldReturnNull()
    {
        // Arrange
        JsonFileGatewayStore store = JsonFileGatewayStore.Create(_directory.FullPath);

        // Act
        GatewayRecord? record = await store.LoadAsync("root");

        // Assert
        record.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "A saved record loads back, snapshot values in any format included")]
    public async Task SaveAsync_ThenLoadAsync_ShouldRoundTrip()
    {
        // Arrange
        JsonFileGatewayStore store = JsonFileGatewayStore.Create(_directory.FullPath);
        var instance = new ResourceManifest("Web", "orders-api", ResourceInstanceKey.New(), [new ResourceActionDescriptor("cors", ResourceActionKind.ReadWrite, IsStateful: true)]);
        byte[] binary = [0xFF, 0x00, 0x7B];
        var saved = new GatewayRecord(
            "root",
            ["east"],
            [new GatewayResourceRecord("Web", "orders-api", "abc:1", [ResourceStateSnapshot.Create("cors", binary)], [instance])]);

        // Act
        await store.SaveAsync(saved);
        GatewayRecord? loaded = await store.LoadAsync("root");

        // Assert
        loaded.ShouldNotBeNull();
        loaded.Gateway.ShouldBe(saved.Gateway);
        loaded.Gateways.ShouldBe(saved.Gateways);
        GatewayResourceRecord resource = loaded.Resources.ShouldHaveSingleItem();
        resource.Revision.ShouldBe("abc:1");
        resource.State.ShouldHaveSingleItem().Value.ToArray().ShouldBe(binary);
        resource.Instances.ShouldHaveSingleItem().ShouldBe(instance with { Actions = resource.Instances[0].Actions });
        resource.Instances[0].Actions.ShouldBe(instance.Actions);
    }

    [Fact(DisplayName = DisplayPrefix + "A save replaces the previous record and leaves no temporary file")]
    public async Task SaveAsync_Twice_ShouldReplaceTheRecord()
    {
        // Arrange
        JsonFileGatewayStore store = JsonFileGatewayStore.Create(_directory.FullPath);

        // Act
        await store.SaveAsync(new GatewayRecord("root", ["east"], []));
        await store.SaveAsync(new GatewayRecord("root", [], []));

        // Assert
        (await store.LoadAsync("root")).ShouldNotBeNull().Gateways.ShouldBeEmpty();
        Directory.GetFiles(store.DirectoryPath).ShouldBe([Path.Combine(store.DirectoryPath, "root.json")]);
    }

    [Theory(DisplayName = DisplayPrefix + "A store needs a directory path")]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutDirectory_ShouldThrow(string directoryPath)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => JsonFileGatewayStore.Create(directoryPath));
    }
}
