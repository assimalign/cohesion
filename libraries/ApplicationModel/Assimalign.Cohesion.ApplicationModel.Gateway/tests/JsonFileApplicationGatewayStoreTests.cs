using System;
using System.Collections.Generic;
using System.IO;
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
        var instance = new ApplicationInstanceManifest("Web", "orders-api", InstanceKey.New(), DeploymentId.New(), [new ApplicationActionDescriptor("cors", ApplicationActionKind.ReadWrite, IsStateful: true)]);
        byte[] binary = [0xFF, 0x00, 0x7B];
        var outputs = new Dictionary<OutputName, string> { [OutputName.Endpoint] = "http://orders-api.root.svc" };
        var saved = new ApplicationGatewayRecord(
            "root",
            [new ApplicationResourceRecord("Web", "orders-api", "abc:1", [ApplicationActionSnapshot.Create("cors", binary)], [instance], outputs)]);

        // Act
        await store.SaveAsync(saved);
        ApplicationGatewayRecord? loaded = await store.LoadAsync("root");

        // Assert
        loaded.ShouldNotBeNull();
        loaded.Gateway.ShouldBe(saved.Gateway);
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
        Directory.GetFiles(store.DirectoryPath).ShouldBe([Path.Combine(store.DirectoryPath, "root.json")]);
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
