using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

public class ApplicationInstancePublisherTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ApplicationInstancePublisher: ";

    private static readonly Uri _endpoint = new("http://127.0.0.1:5100/manifests");

    [Fact(DisplayName = DisplayPrefix + "The manifest is posted to the endpoint in the serializer's format")]
    public async Task PublishAsync_WithManifest_ShouldPostItAsJson()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        ApplicationInstancePublisher publisher = CreatePublisher(handler);
        ApplicationInstanceManifest manifest = CreateManifest(new LogLevelAction());

        // Act
        await publisher.PublishAsync(manifest);

        // Assert
        handler.Method.ShouldBe(HttpMethod.Post);
        handler.RequestUri.ShouldBe(_endpoint);
        handler.MediaType.ShouldBe("application/json");
        string json = Encoding.UTF8.GetString(handler.Body);
        json.ShouldContain("\"provider\":\"Web\"", Case.Sensitive);
        json.ShouldContain("\"name\":\"log-level\",\"kind\":\"ReadWrite\",\"isStateful\":true", Case.Sensitive);
        ApplicationInstanceManifest? received = JsonSerializer.Deserialize(handler.Body, ResourceJsonContext.Default.ApplicationInstanceManifest);
        received.ShouldNotBeNull();
        received.Provider.ShouldBe(manifest.Provider);
        received.Name.ShouldBe(manifest.Name);
        received.Key.ShouldBe(manifest.Key);
        received.Actions.ShouldBe(manifest.Actions);
    }

    [Fact(DisplayName = DisplayPrefix + "The snapshots and lease the gateway answers with are returned")]
    public async Task PublishAsync_WhenGatewayAnswersWithSnapshots_ShouldReturnThem()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"snapshots":[{"action":"log-level","value":{"Level":"Debug"}}],"lease":"00:00:30"}""", Encoding.UTF8, "application/json"),
        });
        ApplicationInstancePublisher publisher = CreatePublisher(handler);

        // Act
        ApplicationInstancePublication publication = await publisher.PublishAsync(CreateManifest(new LogLevelAction()));

        // Assert
        publication.Lease.ShouldBe(TimeSpan.FromSeconds(30));
        ApplicationActionSnapshot snapshot = publication.Snapshots.ShouldHaveSingleItem();
        snapshot.Action.ShouldBe((ActionName)"log-level");
        Encoding.UTF8.GetString(snapshot.Value.Span).ShouldBe("""{"Level":"Debug"}""");
    }

    [Theory(DisplayName = DisplayPrefix + "No content or an empty body means nothing to restore and no lease")]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.OK)]
    public async Task PublishAsync_WhenGatewayAnswersWithNothing_ShouldReturnEmpty(HttpStatusCode status)
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status));
        ApplicationInstancePublisher publisher = CreatePublisher(handler);

        // Act
        ApplicationInstancePublication publication = await publisher.PublishAsync(CreateManifest());

        // Assert
        publication.Snapshots.ShouldBeEmpty();
        publication.Lease.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "A refused publication throws with the gateway's status")]
    public async Task PublishAsync_WhenGatewayRefuses_ShouldThrow()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        ApplicationInstancePublisher publisher = CreatePublisher(handler);

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(() => publisher.PublishAsync(CreateManifest()).AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact(DisplayName = DisplayPrefix + "An answer that is not a publication is refused content")]
    public async Task PublishAsync_WhenGatewayAnswersWithMalformedList_ShouldThrowResourceContentException()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"action":"log-level"}]""", Encoding.UTF8, "application/json"),
        });
        ApplicationInstancePublisher publisher = CreatePublisher(handler);

        // Act & Assert
        await Should.ThrowAsync<ApplicationContentException>(() => publisher.PublishAsync(CreateManifest()).AsTask());
    }

    [Fact(DisplayName = DisplayPrefix + "Each publication gets its own client from the factory and disposes it")]
    public async Task PublishAsync_WithClientFactory_ShouldCreateAndDisposeAClientPerPublication()
    {
        // Arrange — the factory hands out clients over one handler, as an HTTP client factory does.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var clients = new List<TrackingHttpClient>();
        ApplicationInstancePublisher publisher = ApplicationInstancePublisher.Create(new ApplicationInstancePublisherOptions
        {
            Endpoint = _endpoint,
            HttpClientFactory = () =>
            {
                var client = new TrackingHttpClient(handler);
                clients.Add(client);
                return client;
            },
        });
        ApplicationInstanceManifest manifest = CreateManifest();

        // Act
        await publisher.PublishAsync(manifest);
        await publisher.PublishAsync(manifest);

        // Assert
        clients.Count.ShouldBe(2);
        clients.ShouldAllBe(client => client.IsDisposed);
        clients[0].ShouldNotBeSameAs(clients[1]);
    }

    [Fact(DisplayName = DisplayPrefix + "A factory that returns no client fails the publication")]
    public async Task PublishAsync_WhenFactoryReturnsNull_ShouldThrow()
    {
        // Arrange
        ApplicationInstancePublisher publisher = ApplicationInstancePublisher.Create(new ApplicationInstancePublisherOptions
        {
            Endpoint = _endpoint,
            HttpClientFactory = () => null!,
        });

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => publisher.PublishAsync(CreateManifest()).AsTask());
    }

    [Fact(DisplayName = DisplayPrefix + "A resource service restores the state the gateway answers with before its host serves")]
    public async Task StartingAsync_WithGatewaySnapshot_ShouldRestoreTheActionState()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"snapshots":[{"action":"log-level","value":{"Level":"Debug"}}],"lease":"00:00:30"}""", Encoding.UTF8, "application/json"),
        });
        var action = new LogLevelAction();
        ApplicationInstanceService service = ApplicationInstanceService.Create(ApplicationInstanceControlPlane.Create(new TestResource(action), TestSerializer.Json), CreatePublisher(handler));

        // Act
        await service.StartingAsync();
        await service.StopAsync();

        // Assert
        action.Current.ShouldBe(new LogLevelSetting("Debug"));
    }

    [Fact(DisplayName = DisplayPrefix + "A renewal puts the manifest to the endpoint")]
    public async Task RenewAsync_WhenGatewayKnowsTheInstance_ShouldPutTheManifest()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        ApplicationInstancePublisher publisher = CreatePublisher(handler);

        // Act
        bool renewed = await publisher.RenewAsync(CreateManifest());

        // Assert
        renewed.ShouldBeTrue();
        handler.Method.ShouldBe(HttpMethod.Put);
        handler.RequestUri.ShouldBe(_endpoint);
        handler.MediaType.ShouldBe("application/json");
    }

    [Fact(DisplayName = DisplayPrefix + "A renewal the gateway answers with 404 means it does not know the instance")]
    public async Task RenewAsync_WhenGatewayDoesNotKnowTheInstance_ShouldReturnFalse()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        ApplicationInstancePublisher publisher = CreatePublisher(handler);

        // Act
        bool renewed = await publisher.RenewAsync(CreateManifest());

        // Assert
        renewed.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "A refused renewal throws with the gateway's status")]
    public async Task RenewAsync_WhenGatewayRefuses_ShouldThrow()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        ApplicationInstancePublisher publisher = CreatePublisher(handler);

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(() => publisher.RenewAsync(CreateManifest()).AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Theory(DisplayName = DisplayPrefix + "An endpoint must be an absolute http or https URL")]
    [InlineData(null)]
    [InlineData("/manifests")]
    [InlineData("ftp://127.0.0.1/manifests")]
    public void Create_WithInvalidEndpoint_ShouldThrow(string? endpoint)
    {
        // Arrange
        var options = new ApplicationInstancePublisherOptions
        {
            Endpoint = endpoint is null ? null : new Uri(endpoint, UriKind.RelativeOrAbsolute),
        };

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationInstancePublisher.Create(options));
    }

    [Fact(DisplayName = DisplayPrefix + "An in-process endpoint publishes to the publisher registered for it")]
    public async Task PublishAsync_WithInProcessEndpoint_ShouldPublishToTheRegisteredPublisher()
    {
        // Arrange
        var endpoint = new Uri($"inprocess://{Guid.NewGuid():N}/");
        var gateway = new TestPublisher { Lease = TimeSpan.FromSeconds(30) };
        using IDisposable registration = ApplicationInstancePublisher.Register(endpoint, gateway);
        ApplicationInstancePublisher publisher = ApplicationInstancePublisher.Create(new ApplicationInstancePublisherOptions { Endpoint = endpoint });
        ApplicationInstanceManifest manifest = CreateManifest(new LogLevelAction());

        // Act
        ApplicationInstancePublication publication = await publisher.PublishAsync(manifest);
        bool renewed = await publisher.RenewAsync(manifest);

        // Assert
        gateway.Published.ShouldHaveSingleItem().ShouldBeSameAs(manifest);
        publication.Lease.ShouldBe(TimeSpan.FromSeconds(30));
        renewed.ShouldBeTrue();
        gateway.Renewals.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "An in-process endpoint nothing is registered for refuses the publication")]
    public async Task PublishAsync_WithUnregisteredInProcessEndpoint_ShouldThrow()
    {
        // Arrange
        var endpoint = new Uri($"inprocess://{Guid.NewGuid():N}/");
        IDisposable registration = ApplicationInstancePublisher.Register(endpoint, new TestPublisher());
        ApplicationInstancePublisher publisher = ApplicationInstancePublisher.Create(new ApplicationInstancePublisherOptions { Endpoint = endpoint });
        registration.Dispose();

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => publisher.PublishAsync(CreateManifest()).AsTask());
    }

    [Fact(DisplayName = DisplayPrefix + "An in-process endpoint is registered once at a time")]
    public void Register_WithRegisteredEndpoint_ShouldThrow()
    {
        // Arrange
        var endpoint = new Uri($"inprocess://{Guid.NewGuid():N}/");
        using IDisposable registration = ApplicationInstancePublisher.Register(endpoint, new TestPublisher());

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationInstancePublisher.Register(endpoint, new TestPublisher()));
    }

    [Fact(DisplayName = DisplayPrefix + "Only an in-process URL can be registered")]
    public void Register_WithHttpEndpoint_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationInstancePublisher.Register(_endpoint, new TestPublisher()));
    }

    private static ApplicationInstancePublisher CreatePublisher(HttpMessageHandler handler)
    {
        return ApplicationInstancePublisher.Create(new ApplicationInstancePublisherOptions
        {
            Endpoint = _endpoint,
            HttpClientFactory = () => new HttpClient(handler, disposeHandler: false),
            Serializer = TestSerializer.Json,
        });
    }

    private static ApplicationInstanceManifest CreateManifest(params IApplicationInstanceAction[] actions)
    {
        return ApplicationInstanceControlPlane.Create(new TestResource(actions), TestSerializer.Json).Manifest;
    }
}
