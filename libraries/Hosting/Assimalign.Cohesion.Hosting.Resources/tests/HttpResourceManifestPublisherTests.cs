using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class HttpResourceManifestPublisherTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - HttpResourceManifestPublisher: ";

    private static readonly Uri _endpoint = new("http://127.0.0.1:5100/manifests");

    [Fact(DisplayName = DisplayPrefix + "The manifest is posted to the endpoint as JSON")]
    public async Task PublishAsync_WithManifest_ShouldPostItAsJson()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler);
        using HttpResourceManifestPublisher publisher = HttpResourceManifestPublisher.Create(_endpoint, client);
        ResourceManifest manifest = ResourceControlPlane.Create(new TestResource(new LogLevelAction()), publisher).Manifest;

        // Act
        await publisher.PublishAsync(manifest);

        // Assert
        handler.Method.ShouldBe(HttpMethod.Post);
        handler.RequestUri.ShouldBe(_endpoint);
        handler.MediaType.ShouldBe("application/json");
        string json = Encoding.UTF8.GetString(handler.Body);
        json.ShouldContain("\"provider\":\"Web\"", Case.Sensitive);
        json.ShouldContain("\"name\":\"log-level\",\"kind\":\"ReadWrite\",\"isStateful\":true", Case.Sensitive);
        ResourceManifest? received = JsonSerializer.Deserialize(handler.Body, ResourceJsonContext.Default.ResourceManifest);
        received.ShouldNotBeNull();
        received.Provider.ShouldBe(manifest.Provider);
        received.Name.ShouldBe(manifest.Name);
        received.Key.ShouldBe(manifest.Key);
        received.Actions.ShouldBe(manifest.Actions);
    }

    [Fact(DisplayName = DisplayPrefix + "The snapshots the gateway answers with are returned")]
    public async Task PublishAsync_WhenGatewayAnswersWithSnapshots_ShouldReturnThem()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"action":"log-level","value":{"Level":"Debug"}}]""", Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        using HttpResourceManifestPublisher publisher = HttpResourceManifestPublisher.Create(_endpoint, client);
        ResourceManifest manifest = ResourceControlPlane.Create(new TestResource(new LogLevelAction()), publisher).Manifest;

        // Act
        IReadOnlyList<ResourceStateSnapshot> snapshots = await publisher.PublishAsync(manifest);

        // Assert
        ResourceStateSnapshot snapshot = snapshots.ShouldHaveSingleItem();
        snapshot.Action.ShouldBe((ResourceActionName)"log-level");
        Encoding.UTF8.GetString(snapshot.Value.Span).ShouldBe("""{"Level":"Debug"}""");
    }

    [Theory(DisplayName = DisplayPrefix + "No content or an empty body means nothing to restore")]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.OK)]
    public async Task PublishAsync_WhenGatewayAnswersWithNothing_ShouldReturnEmpty(HttpStatusCode status)
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status));
        using var client = new HttpClient(handler);
        using HttpResourceManifestPublisher publisher = HttpResourceManifestPublisher.Create(_endpoint, client);
        ResourceManifest manifest = ResourceControlPlane.Create(new TestResource(), publisher).Manifest;

        // Act
        IReadOnlyList<ResourceStateSnapshot> snapshots = await publisher.PublishAsync(manifest);

        // Assert
        snapshots.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A refused publication throws with the gateway's status")]
    public async Task PublishAsync_WhenGatewayRefuses_ShouldThrow()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        using HttpResourceManifestPublisher publisher = HttpResourceManifestPublisher.Create(_endpoint, client);
        ResourceManifest manifest = ResourceControlPlane.Create(new TestResource(), publisher).Manifest;

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(() => publisher.PublishAsync(manifest).AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact(DisplayName = DisplayPrefix + "A control plane restores the state the gateway answers with before its host serves")]
    public async Task StartAsync_WithGatewaySnapshot_ShouldRestoreTheActionState()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"action":"log-level","value":{"Level":"Debug"}}]""", Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        using HttpResourceManifestPublisher publisher = HttpResourceManifestPublisher.Create(_endpoint, client);
        var action = new LogLevelAction();
        ResourceControlPlane controlPlane = ResourceControlPlane.Create(new TestResource(action), publisher);

        // Act
        await controlPlane.StartAsync();

        // Assert
        action.Current.ShouldBe(new LogLevelSetting("Debug"));
    }

    [Theory(DisplayName = DisplayPrefix + "An endpoint must be an absolute http or https URL")]
    [InlineData("/manifests")]
    [InlineData("ftp://127.0.0.1/manifests")]
    public void Create_WithInvalidEndpoint_ShouldThrow(string endpoint)
    {
        // Act / Assert
        Should.Throw<ArgumentException>(() => HttpResourceManifestPublisher.Create(new Uri(endpoint, UriKind.RelativeOrAbsolute)));
    }

    [Fact(DisplayName = DisplayPrefix + "No manifest endpoint variable means no gateway manages the instance")]
    public void FromEnvironment_WhenVariableIsUnset_ShouldReturnNull()
    {
        // Arrange
        string? previous = Environment.GetEnvironmentVariable(AppEnvironment.Variables.ManifestEndpoint);
        Environment.SetEnvironmentVariable(AppEnvironment.Variables.ManifestEndpoint, null);
        try
        {
            // Act
            HttpResourceManifestPublisher? publisher = HttpResourceManifestPublisher.FromEnvironment();

            // Assert
            publisher.ShouldBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppEnvironment.Variables.ManifestEndpoint, previous);
        }
    }
}
