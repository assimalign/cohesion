using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.SecretStore.Client.Tests.TestObjects;

namespace Assimalign.Cohesion.SecretStore.Client.Tests;

public class SecretStoreClientTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore] - GetSecretAsync: Should send an authenticated request and return bytes")]
    public async Task GetSecretAsync_WhenEndpointReturnsBytes_ShouldReturnBytes()
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
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("classified"))
            });
        });
        using var transport = new HttpMessageInvoker(handler);
        ISecretStoreClient client = SecretStoreClient.Create(
            new Uri("https://secrets.test:8443/api"),
            new ClientCredential("bootstrap-token"),
            transport);

        // Act
        ReadOnlyMemory<byte> result = await client.GetSecretAsync("apps/api/password", CancellationToken.None);

        // Assert
        Encoding.UTF8.GetString(result.ToArray()).ShouldBe("classified");
        observedMethod.ShouldBe("GET");
        observedUri.ShouldBe("https://secrets.test:8443/api/cohesion/v1/secrets?path=apps%2Fapi%2Fpassword");
        observedAuthorization.ShouldBe("Bearer bootstrap-token");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore] - GetCertificateAsync: Should return PEM from the endpoint")]
    public async Task GetCertificateAsync_WhenEndpointReturnsPem_ShouldReturnPem()
    {
        // Arrange
        const string pem = "-----BEGIN CERTIFICATE-----\nAQID\n-----END CERTIFICATE-----";
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(pem, Encoding.ASCII, "application/x-pem-file")
            }));
        using var transport = new HttpMessageInvoker(handler);
        ISecretStoreClient client = SecretStoreClient.Create(
            new Uri("http://127.0.0.1:5080"),
            new ClientCredential("bootstrap-token"),
            transport);

        // Act
        string result = await client.GetCertificateAsync("certs/appa-api", CancellationToken.None);

        // Assert
        result.ShouldBe(pem);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore] - SendCommandAsync: Should send the source-generated command envelope")]
    public async Task SendCommandAsync_WhenEndpointAcceptsCommand_ShouldSendJsonEnvelope()
    {
        // Arrange
        string? observedBody = null;
        string? observedAccept = null;
        string? observedContentType = null;
        string? observedMethod = null;
        string? observedUri = null;
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            observedAccept = request.Headers.Accept.ToString();
            observedUri = request.RequestUri?.AbsoluteUri;
            observedMethod = request.Method.Method;
            observedContentType = request.Content?.Headers.ContentType?.MediaType;
            observedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var transport = new HttpMessageInvoker(handler);
        ISecretStoreClient client = SecretStoreClient.Create(
            new Uri("https://secrets.test:443"),
            new ClientCredential("bootstrap-token"),
            transport);
        var command = new ResourceCommand(
            "command-1",
            "secretstore.add-secret",
            "appa",
            "apps/api/password",
            Encoding.UTF8.GetBytes("{\"value\":\"classified\"}"));

        // Act
        await client.SendCommandAsync(command, CancellationToken.None);

        // Assert
        observedMethod.ShouldBe("POST");
        observedUri.ShouldBe("https://secrets.test/cohesion/v1/commands");
        observedAccept.ShouldBe("application/octet-stream");
        observedContentType.ShouldBe("application/json");
        using JsonDocument document = JsonDocument.Parse(observedBody!);
        document.RootElement.GetProperty("id").GetString().ShouldBe("command-1");
        document.RootElement.GetProperty("kind").GetString().ShouldBe("secretstore.add-secret");
        document.RootElement.GetProperty("owner").GetString().ShouldBe("appa");
        document.RootElement.GetProperty("key").GetString().ShouldBe("apps/api/password");
        document.RootElement.GetProperty("payload").GetString().ShouldBe(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"value\":\"classified\"}")));
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore] - Create: Should reject a non-HTTP endpoint")]
    public void Create_WhenEndpointIsNotHttp_ShouldThrowArgumentException()
    {
        // Arrange
        var endpoint = new Uri("tcp://secrets.test:8443");
        var credential = new ClientCredential("bootstrap-token");

        // Act
        Action action = () => SecretStoreClient.Create(endpoint, credential);

        // Assert
        Should.Throw<ArgumentException>(action).ParamName.ShouldBe("endpoint");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore] - Create: Should reject an endpoint with a query")]
    public void Create_WhenEndpointHasQuery_ShouldThrowArgumentException()
    {
        // Arrange
        var endpoint = new Uri("https://secrets.test:8443?q=1");
        var credential = new ClientCredential("bootstrap-token");

        // Act
        Action action = () => SecretStoreClient.Create(endpoint, credential);

        // Assert
        Should.Throw<ArgumentException>(action).ParamName.ShouldBe("endpoint");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore] - Create: Should reject a null endpoint")]
    public void Create_WhenEndpointIsNull_ShouldThrowArgumentNullException()
    {
        // Arrange
        var credential = new ClientCredential("bootstrap-token");

        // Act
        Action action = () => SecretStoreClient.Create(null!, credential);

        // Assert
        Should.Throw<ArgumentNullException>(action).ParamName.ShouldBe("endpoint");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore] - ClientCredential: Should redact the token when formatted")]
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
