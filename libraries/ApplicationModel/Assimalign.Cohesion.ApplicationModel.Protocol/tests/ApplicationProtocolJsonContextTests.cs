using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Protocol.Tests;

public class ApplicationProtocolJsonContextTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationProtocolJsonContext: ";

    [Fact(DisplayName = DisplayPrefix + "A manifest survives a round trip")]
    public void Serialize_Manifest_ShouldRoundTrip()
    {
        // Arrange
        ApplicationInstanceManifest manifest = TestManifests.Instance(DeploymentId.New(), new Uri("http://10.0.0.5:9090/"));

        // Act
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, ApplicationProtocolJsonContext.Default.ApplicationInstanceManifest);
        ApplicationInstanceManifest? read = JsonSerializer.Deserialize(json, ApplicationProtocolJsonContext.Default.ApplicationInstanceManifest);

        // Assert
        read.ShouldNotBeNull();
        read.Provider.ShouldBe(manifest.Provider);
        read.Name.ShouldBe(manifest.Name);
        read.Key.ShouldBe(manifest.Key);
        read.Deployment.ShouldBe(manifest.Deployment);
        read.ControlPlaneEndpoint.ShouldBe(manifest.ControlPlaneEndpoint);
        read.Actions.ShouldBe(manifest.Actions);
    }

    [Fact(DisplayName = DisplayPrefix + "A snapshot is written as its action and its embedded value")]
    public void Serialize_Snapshots_ShouldEmbedTheValue()
    {
        // Arrange
        IReadOnlyList<ApplicationActionSnapshot> snapshots =
        [
            ApplicationActionSnapshot.Create("cors", Encoding.UTF8.GetBytes("""{"origins":["https://shop.example"]}""")),
        ];

        // Act
        string json = JsonSerializer.Serialize(snapshots, ApplicationProtocolJsonContext.Default.IReadOnlyListApplicationActionSnapshot);
        IReadOnlyList<ApplicationActionSnapshot>? read = JsonSerializer.Deserialize(json, ApplicationProtocolJsonContext.Default.IReadOnlyListApplicationActionSnapshot);

        // Assert
        json.ShouldBe("""[{"action":"cors","value":{"origins":["https://shop.example"]}}]""");
        ApplicationActionSnapshot snapshot = read.ShouldNotBeNull().ShouldHaveSingleItem();
        snapshot.Action.ShouldBe((ActionName)"cors");
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
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(json, ApplicationProtocolJsonContext.Default.IReadOnlyListApplicationActionSnapshot));
    }

    [Fact(DisplayName = DisplayPrefix + "A publication is written as its snapshots and its lease")]
    public void Serialize_Publication_ShouldCarrySnapshotsAndLease()
    {
        // Arrange
        var publication = new ApplicationInstancePublication(
            [ApplicationActionSnapshot.Create("cors", """{"origins":["https://shop.example"]}"""u8.ToArray())],
            TimeSpan.FromSeconds(30));

        // Act
        string json = JsonSerializer.Serialize(publication, ApplicationProtocolJsonContext.Default.ApplicationInstancePublication);
        ApplicationInstancePublication? read = JsonSerializer.Deserialize(json, ApplicationProtocolJsonContext.Default.ApplicationInstancePublication);

        // Assert
        json.ShouldBe("""{"snapshots":[{"action":"cors","value":{"origins":["https://shop.example"]}}],"lease":"00:00:30"}""");
        read.ShouldNotBeNull().Lease.ShouldBe(TimeSpan.FromSeconds(30));
        read.Snapshots.ShouldHaveSingleItem().Action.ShouldBe((ActionName)"cors");
    }

    [Fact(DisplayName = DisplayPrefix + "A result with an embedded output and a state snapshot survives a round trip")]
    public void Deserialize_ResultWithOutputAndState_ShouldRoundTrip()
    {
        // Arrange
        const string json = """{"status":"Succeeded","output":{"origins":["https://partner.example"]},"state":{"action":"cors","value":{"origins":["https://partner.example"]}}}""";

        // Act
        ApplicationActionResult? read = JsonSerializer.Deserialize(json, ApplicationProtocolJsonContext.Default.ApplicationActionResult);
        string written = JsonSerializer.Serialize(read!, ApplicationProtocolJsonContext.Default.ApplicationActionResult);

        // Assert
        read.ShouldNotBeNull();
        read.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        Encoding.UTF8.GetString(read.Output.Span).ShouldBe("""{"origins":["https://partner.example"]}""");
        ApplicationActionSnapshot state = read.State.ShouldNotBeNull();
        state.Action.ShouldBe((ActionName)"cors");
        Encoding.UTF8.GetString(state.Value.Span).ShouldBe("""{"origins":["https://partner.example"]}""");
        written.ShouldBe(json);
    }

    [Fact(DisplayName = DisplayPrefix + "A rejection is written with its reason and nothing else")]
    public void Serialize_Rejection_ShouldCarryOnlyStatusAndReason()
    {
        // Act
        string json = JsonSerializer.Serialize(ApplicationActionResult.Rejected("A log level is required."), ApplicationProtocolJsonContext.Default.ApplicationActionResult);
        ApplicationActionResult? read = JsonSerializer.Deserialize(json, ApplicationProtocolJsonContext.Default.ApplicationActionResult);

        // Assert
        json.ShouldBe("""{"status":"Rejected","reason":"A log level is required."}""");
        read.ShouldNotBeNull().Status.ShouldBe(ApplicationActionStatus.Rejected);
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
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(json, ApplicationProtocolJsonContext.Default.ApplicationActionResult));
    }
}
