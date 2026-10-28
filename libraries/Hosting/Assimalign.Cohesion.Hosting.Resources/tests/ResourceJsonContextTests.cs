using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class ResourceJsonContextTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ResourceJsonContext: ";

    [Fact(DisplayName = DisplayPrefix + "A manifest survives a round trip")]
    public void Serialize_Manifest_ShouldRoundTrip()
    {
        // Arrange
        var resource = new TestResource(new LogLevelAction());
        ResourceManifest manifest = ResourceControlPlane.Create(resource, new TestPublisher()).Manifest;

        // Act
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, ResourceJsonContext.Default.ResourceManifest);
        ResourceManifest? read = JsonSerializer.Deserialize(json, ResourceJsonContext.Default.ResourceManifest);

        // Assert
        read.ShouldNotBeNull();
        read.Provider.ShouldBe(manifest.Provider);
        read.Name.ShouldBe(manifest.Name);
        read.Key.ShouldBe(manifest.Key);
        read.Actions.ShouldBe(manifest.Actions);
    }

    [Fact(DisplayName = DisplayPrefix + "A snapshot is written as its action and its embedded value")]
    public void Serialize_Snapshots_ShouldEmbedTheValue()
    {
        // Arrange
        IReadOnlyList<ResourceStateSnapshot> snapshots =
        [
            ResourceStateSnapshot.Create("cors", Encoding.UTF8.GetBytes("""{"origins":["https://shop.example"]}""")),
        ];

        // Act
        string json = JsonSerializer.Serialize(snapshots, ResourceJsonContext.Default.IReadOnlyListResourceStateSnapshot);
        IReadOnlyList<ResourceStateSnapshot>? read = JsonSerializer.Deserialize(json, ResourceJsonContext.Default.IReadOnlyListResourceStateSnapshot);

        // Assert
        json.ShouldBe("""[{"action":"cors","value":{"origins":["https://shop.example"]}}]""");
        ResourceStateSnapshot snapshot = read.ShouldNotBeNull().ShouldHaveSingleItem();
        snapshot.Action.ShouldBe((ResourceActionName)"cors");
        Encoding.UTF8.GetString(snapshot.Value.Span).ShouldBe("""{"origins":["https://shop.example"]}""");
    }

    [Theory(DisplayName = DisplayPrefix + "A snapshot without an action or value, or with an invalid action name, is refused")]
    [InlineData("""[{"value":{}}]""")]
    [InlineData("""[{"action":"cors"}]""")]
    [InlineData("""[{"action":42,"value":{}}]""")]
    [InlineData("""[{"action":"Not A Name","value":{}}]""")]
    [InlineData("""["cors"]""")]
    public void Deserialize_MalformedSnapshot_ShouldThrow(string json)
    {
        // Act / Assert
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(json, ResourceJsonContext.Default.IReadOnlyListResourceStateSnapshot));
    }
}
