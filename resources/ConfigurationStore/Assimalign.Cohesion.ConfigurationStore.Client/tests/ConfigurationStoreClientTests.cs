using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ConfigurationStore.Client.Tests.TestObjects;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ConfigurationStore.Client.Tests;

public class ConfigurationStoreClientTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - GetNamespaceAsync: Should send an authenticated request and return values")]
    public async Task GetNamespaceAsync_WhenEndpointReturnsObject_ShouldReturnValues()
    {
        // Arrange
        string? observedMethod = null;
        string? observedUri = null;
        string? observedAuthorization = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            observedMethod = request.Method.Method;
            observedUri = request.RequestUri?.AbsoluteUri;
            observedAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"mode\":\"production\",\"optional\":null}",
                    Encoding.UTF8,
                    "application/json")
            });
        });
        using var transport = new HttpMessageInvoker(handler);
        IConfigurationStoreClient client = ConfigurationStoreClient.Create(
            new EndpointAddress("https", "configuration.test", 8443, "/api"),
            new ClientCredential("bootstrap-token"),
            transport);

        // Act
        IReadOnlyDictionary<string, string?> result = await client.GetNamespaceAsync(
            "apps/api",
            CancellationToken.None);

        // Assert
        result.Count.ShouldBe(2);
        result["mode"].ShouldBe("production");
        result["optional"].ShouldBeNull();
        Action mutation = () => ((IDictionary<string, string?>)result).Add("late", "change");
        Should.Throw<NotSupportedException>(mutation);
        observedMethod.ShouldBe("GET");
        observedUri.ShouldBe(
            "https://configuration.test:8443/api/cohesion/v1/namespaces?name=apps%2Fapi");
        observedAuthorization.ShouldBe("Bearer bootstrap-token");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - SendCommandAsync: Should send the source-generated command envelope")]
    public async Task SendCommandAsync_WhenEndpointAcceptsCommand_ShouldSendJsonEnvelope()
    {
        // Arrange
        string? observedBody = null;
        string? observedAccept = null;
        string? observedMethod = null;
        string? observedUri = null;
        string? observedContentType = null;
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            observedAccept = request.Headers.Accept.ToString();
            observedMethod = request.Method.Method;
            observedUri = request.RequestUri?.AbsoluteUri;
            observedContentType = request.Content?.Headers.ContentType?.MediaType;
            observedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var transport = new HttpMessageInvoker(handler);
        IConfigurationStoreClient client = ConfigurationStoreClient.Create(
            new EndpointAddress("https", "configuration.test", 8443),
            new ClientCredential("bootstrap-token"),
            transport);
        var command = new ResourceCommand(
            "command-1",
            "configurationstore.set-value",
            "appa",
            "apps/api/mode",
            Encoding.UTF8.GetBytes("{\"value\":\"production\"}"));

        // Act
        await client.SendCommandAsync(command, CancellationToken.None);

        // Assert
        observedMethod.ShouldBe("POST");
        observedUri.ShouldBe(
            "https://configuration.test:8443/cohesion/v1/commands");
        observedAccept.ShouldBe("application/octet-stream");
        observedContentType.ShouldBe("application/json");
        using JsonDocument document = JsonDocument.Parse(observedBody!);
        document.RootElement.GetProperty("id").GetString().ShouldBe("command-1");
        document.RootElement.GetProperty("kind").GetString().ShouldBe("configurationstore.set-value");
        document.RootElement.GetProperty("owner").GetString().ShouldBe("appa");
        document.RootElement.GetProperty("key").GetString().ShouldBe("apps/api/mode");
        document.RootElement.GetProperty("payload").GetString().ShouldBe(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"value\":\"production\"}")));
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - GetNamespaceAsync: Should reject a redirect response")]
    public async Task GetNamespaceAsync_WhenEndpointRedirects_ShouldThrowHttpRequestException()
    {
        // Arrange
        int requestCount = 0;
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers =
                {
                    Location = new Uri("https://other.test/cohesion/v1/namespaces")
                }
            });
        });
        using var transport = new HttpMessageInvoker(handler);
        IConfigurationStoreClient client = ConfigurationStoreClient.Create(
            new EndpointAddress("https", "configuration.test", 8443),
            new ClientCredential("bootstrap-token"),
            transport);

        // Act
        Func<Task> action = () => client.GetNamespaceAsync("apps/api", CancellationToken.None);

        // Assert
        await Should.ThrowAsync<HttpRequestException>(action);
        requestCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - GetNamespaceAsync: Should reject a null namespace document")]
    public async Task GetNamespaceAsync_WhenEndpointReturnsNull_ShouldThrowJsonException()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            }));
        using var transport = new HttpMessageInvoker(handler);
        IConfigurationStoreClient client = ConfigurationStoreClient.Create(
            new EndpointAddress("https", "configuration.test", 8443),
            new ClientCredential("bootstrap-token"),
            transport);

        // Act
        Func<Task> action = () => client.GetNamespaceAsync("apps/api", CancellationToken.None);

        // Assert
        await Should.ThrowAsync<JsonException>(action);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - Create: Should reject a non-HTTP endpoint")]
    public void Create_WhenEndpointIsNotHttp_ShouldThrowArgumentException()
    {
        // Arrange
        var endpoint = new EndpointAddress("tcp", "configuration.test", 8443);
        var credential = new ClientCredential("bootstrap-token");

        // Act
        Action action = () => ConfigurationStoreClient.Create(endpoint, credential);

        // Assert
        Should.Throw<ArgumentException>(action).ParamName.ShouldBe("endpoint");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - ClientCredential: Should redact the token when formatted")]
    public void ClientCredential_WhenFormatted_ShouldRedactToken()
    {
        // Arrange
        var credential = new ClientCredential("do-not-log-this-token");

        // Act
        string result = credential.ToString();

        // Assert
        result.ShouldNotContain("do-not-log-this-token", Case.Sensitive);
        result.ShouldContain("[redacted]", Case.Sensitive);
    }
}
