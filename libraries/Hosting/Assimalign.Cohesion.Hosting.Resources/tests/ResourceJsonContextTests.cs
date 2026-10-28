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
        ResourceManifest manifest = ResourceControlPlane.Create(resource, new TestPublisher(), TestSerializer.Json).Manifest;

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

    [Fact(DisplayName = DisplayPrefix + "A result with an embedded output and a state snapshot survives a round trip")]
    public void Deserialize_ResultWithOutputAndState_ShouldRoundTrip()
    {
        // Arrange
        const string json = """{"status":"Succeeded","output":{"origins":["https://partner.example"]},"state":{"action":"cors","value":{"origins":["https://partner.example"]}}}""";

        // Act
        ResourceActionResult? read = JsonSerializer.Deserialize(json, ResourceJsonContext.Default.ResourceActionResult);
        string written = JsonSerializer.Serialize(read!, ResourceJsonContext.Default.ResourceActionResult);

        // Assert
        read.ShouldNotBeNull();
        read.Status.ShouldBe(ResourceActionStatus.Succeeded);
        Encoding.UTF8.GetString(read.Output.Span).ShouldBe("""{"origins":["https://partner.example"]}""");
        ResourceStateSnapshot state = read.State.ShouldNotBeNull();
        state.Action.ShouldBe((ResourceActionName)"cors");
        Encoding.UTF8.GetString(state.Value.Span).ShouldBe("""{"origins":["https://partner.example"]}""");
        written.ShouldBe(json);
    }

    [Fact(DisplayName = DisplayPrefix + "A rejection is written with its reason and nothing else")]
    public void Serialize_Rejection_ShouldCarryOnlyStatusAndReason()
    {
        // Act
        string json = JsonSerializer.Serialize(ResourceActionResult.Rejected("A log level is required."), ResourceJsonContext.Default.ResourceActionResult);
        ResourceActionResult? read = JsonSerializer.Deserialize(json, ResourceJsonContext.Default.ResourceActionResult);

        // Assert
        json.ShouldBe("""{"status":"Rejected","reason":"A log level is required."}""");
        read.ShouldNotBeNull().Status.ShouldBe(ResourceActionStatus.Rejected);
        read.Reason.ShouldBe("A log level is required.");
        read.Output.IsEmpty.ShouldBeTrue();
        read.State.ShouldBeNull();
    }

    [Theory(DisplayName = DisplayPrefix + "A result without a known status, or a rejection without a reason, is refused")]
    [InlineData("""{}""")]
    [InlineData("""{"status":"Unavailable"}""")]
    [InlineData("""{"status":1}""")]
    [InlineData("""{"status":"Rejected"}""")]
    [InlineData("""{"status":"Succeeded","state":{"action":"cors"}}""")]
    [InlineData("""[]""")]
    public void Deserialize_MalformedResult_ShouldThrow(string json)
    {
        // Act / Assert
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(json, ResourceJsonContext.Default.ResourceActionResult));
    }
}
