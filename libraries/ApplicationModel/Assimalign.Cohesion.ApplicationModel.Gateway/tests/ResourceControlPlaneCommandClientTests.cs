using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Verifies the generic control-plane command client against a loopback endpoint: the wire request
/// must equal what the retired per-area adapters sent, and the response mapping must be unchanged.
/// </summary>
public sealed class ResourceControlPlaneCommandClientTests
{
    private const string token = "resource-scoped-token";
    private const string payloadJson = "{\"name\":\"orders\"}";

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: Serves every resource kind and is the only default registration")]
    public void ResourceKind_DefaultOptions_ShouldBeSoleCatchAllClient()
    {
        // Arrange
        var options = new ApplicationGatewayOptions();

        // Act
        IGatewayResourceCommandClient client = options.CommandClients.ShouldHaveSingleItem();

        // Assert
        IGatewayResourceCommandClient.AnyKind.ShouldBe("*");
        client.ShouldBeOfType<ResourceControlPlaneCommandClient>();
        client.ResourceKind.ShouldBe(IGatewayResourceCommandClient.AnyKind);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: Apply posts the command envelope with bearer and JSON headers")]
    public async Task ApplyAsync_ControlPlaneAddress_ShouldPostCommandEnvelope()
    {
        // Arrange
        await using var endpoint = new CommandEndpointListener();
        var client = new ResourceControlPlaneCommandClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        ResourceCommandResult result = await client.ApplyAsync(
            endpoint.Address("/cohesion/v1"), token, CreateCommand(), null, cancellation.Token);
        RecordedCommandRequest request = await endpoint.Received.WaitAsync(cancellation.Token);

        // Assert
        request.Method.ShouldBe("POST");
        request.Target.ShouldBe("/cohesion/v1/commands");
        request.Headers["Authorization"].ShouldBe("Bearer " + token);
        request.Headers["Content-Type"].ShouldBe("application/json");
        request.Headers.ContainsKey("Accept").ShouldBeFalse();
        Encoding.UTF8.GetString(request.Body).ShouldBe(ExpectedEnvelope());
        result.Status.ShouldBe(ResourceCommandStatus.Applied);
        result.Detail.ShouldBe("Applied");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: Delete sends the same envelope and reports Deleted")]
    public async Task DeleteAsync_ControlPlaneAddress_ShouldSendDeleteAndReportDeleted()
    {
        // Arrange
        await using var endpoint = new CommandEndpointListener(204, "No Content");
        var client = new ResourceControlPlaneCommandClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        ResourceCommandResult result = await client.DeleteAsync(
            endpoint.Address("/cohesion/v1"), token, CreateCommand(), null, cancellation.Token);
        RecordedCommandRequest request = await endpoint.Received.WaitAsync(cancellation.Token);

        // Assert
        request.Method.ShouldBe("DELETE");
        request.Target.ShouldBe("/cohesion/v1/commands");
        request.Headers["Authorization"].ShouldBe("Bearer " + token);
        request.Headers["Content-Type"].ShouldBe("application/json");
        request.Headers.ContainsKey("Accept").ShouldBeFalse();
        Encoding.UTF8.GetString(request.Body).ShouldBe(ExpectedEnvelope());
        result.Status.ShouldBe(ResourceCommandStatus.Applied);
        result.Detail.ShouldBe("Deleted");
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: The commands route is appended to the manifest control-plane path")]
    [InlineData("/cohesion/v1", "/cohesion/v1/commands")]
    [InlineData("/cohesion/v1/", "/cohesion/v1/commands")]
    [InlineData("/custom/control", "/custom/control/commands")]
    [InlineData("/", "/commands")]
    public async Task ApplyAsync_ControlPlanePath_ShouldAppendCommandsRoute(string path, string expected)
    {
        // Arrange
        await using var endpoint = new CommandEndpointListener();
        var client = new ResourceControlPlaneCommandClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await client.ApplyAsync(endpoint.Address(path), token, CreateCommand(), null, cancellation.Token);
        RecordedCommandRequest request = await endpoint.Received.WaitAsync(cancellation.Token);

        // Assert
        request.Target.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: A JSON observation overrides the success status and detail")]
    [InlineData("{\"status\":\"Applied\",\"detail\":\"database created\"}", ResourceCommandStatus.Applied, "database created")]
    [InlineData("{\"status\":\"Rejected\",\"detail\":\"database principal runtime seam is unavailable\"}", ResourceCommandStatus.Rejected, "database principal runtime seam is unavailable")]
    [InlineData("{\"status\":\"Pending\"}", ResourceCommandStatus.Rejected, "Pending")]
    [InlineData("{\"detail\":\"already present\"}", ResourceCommandStatus.Applied, "already present")]
    public async Task ApplyAsync_JsonObservation_ShouldOverrideStatusAndDetail(
        string body, ResourceCommandStatus expectedStatus, string expectedDetail)
    {
        // Arrange
        await using var endpoint = new CommandEndpointListener(200, "OK", "application/json; charset=utf-8", body);
        var client = new ResourceControlPlaneCommandClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        ResourceCommandResult result = await client.ApplyAsync(
            endpoint.Address("/cohesion/v1"), token, CreateCommand(), null, cancellation.Token);

        // Assert
        result.Status.ShouldBe(expectedStatus);
        result.Detail.ShouldBe(expectedDetail);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: A refusal status is Rejected with the body detail or a neutral HTTP detail")]
    [InlineData(403, "Forbidden", null, null, "Command 'database.add-database' was refused: HTTP 403 Forbidden.")]
    [InlineData(500, "Internal Server Error", "text/plain", "boom", "Command 'database.add-database' was refused: HTTP 500 Internal Server Error.")]
    [InlineData(409, "Conflict", "application/json", "{\"status\":\"Rejected\",\"detail\":\"key is owned by application 'other'\"}", "key is owned by application 'other'")]
    [InlineData(409, "Conflict", "application/json", "{\"status\":\"Applied\"}", "Command 'database.add-database' was refused: HTTP 409 Conflict.")]
    public async Task ApplyAsync_RefusalStatus_ShouldReject(
        int statusCode, string reasonPhrase, string? contentType, string? body, string expectedDetail)
    {
        // Arrange
        await using var endpoint = new CommandEndpointListener(statusCode, reasonPhrase, contentType, body);
        var client = new ResourceControlPlaneCommandClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        ResourceCommandResult result = await client.ApplyAsync(
            endpoint.Address("/cohesion/v1"), token, CreateCommand(), null, cancellation.Token);

        // Assert
        result.Status.ShouldBe(ResourceCommandStatus.Rejected);
        result.Detail.ShouldBe(expectedDetail);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: Delete refusal reports the neutral HTTP detail")]
    public async Task DeleteAsync_RefusalStatus_ShouldReject()
    {
        // Arrange
        await using var endpoint = new CommandEndpointListener(401, "Unauthorized");
        var client = new ResourceControlPlaneCommandClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        ResourceCommandResult result = await client.DeleteAsync(
            endpoint.Address("/cohesion/v1"), token, CreateCommand(), null, cancellation.Token);

        // Assert
        result.Status.ShouldBe(ResourceCommandStatus.Rejected);
        result.Detail.ShouldBe("Command 'database.add-database' was refused: HTTP 401 Unauthorized.");
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: HTTPS delivery uses the supplied server certificate validator")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyAsync_HttpsTarget_ShouldApplyServerCertificateValidator(bool trusted)
    {
        // Arrange
        using X509Certificate2 certificate = CreateServerCertificate();
        await using var endpoint = new CommandEndpointListener(certificate: certificate);
        var client = new ResourceControlPlaneCommandClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int validations = 0;
        RemoteCertificateValidationCallback validator = (_, presented, _, _) =>
        {
            Interlocked.Increment(ref validations);
            return trusted && presented is not null &&
                presented.GetCertHashString() == certificate.GetCertHashString();
        };
        Uri address = endpoint.Address("/cohesion/v1");

        // Act
        Func<Task<ResourceCommandResult>> apply = async () =>
            await client.ApplyAsync(address, token, CreateCommand(), validator, cancellation.Token);

        // Assert
        address.Scheme.ShouldBe(Uri.UriSchemeHttps);
        if (trusted)
        {
            (await apply()).Status.ShouldBe(ResourceCommandStatus.Applied);
            (await endpoint.Received.WaitAsync(cancellation.Token)).Target.ShouldBe("/cohesion/v1/commands");
        }
        else
        {
            await Should.ThrowAsync<HttpRequestException>(apply);
            endpoint.Received.IsCompleted.ShouldBeFalse();
        }
        validations.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: Invalid addresses and credentials fail before any request")]
    [InlineData("ftp://127.0.0.1:2121/cohesion/v1", token)]
    [InlineData("http://127.0.0.1:8080/cohesion/v1?x=1", token)]
    [InlineData("http://127.0.0.1:8080/cohesion/v1", " ")]
    public async Task ApplyAsync_InvalidArguments_ShouldThrowArgumentException(string address, string bearerToken)
    {
        // Arrange
        var client = new ResourceControlPlaneCommandClient();

        // Act
        Func<Task> apply = async () =>
            await client.ApplyAsync(new Uri(address), bearerToken, CreateCommand(), null, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<ArgumentException>(apply);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: A blank command identity field fails before any request")]
    [InlineData(" ", "database.add-database", "appa", "orders")]
    [InlineData("cmd-1", "", "appa", "orders")]
    [InlineData("cmd-1", "database.add-database", " ", "orders")]
    [InlineData("cmd-1", "database.add-database", "appa", "")]
    public async Task ApplyAsync_BlankCommandField_ShouldThrowArgumentException(string id, string kind, string owner, string key)
    {
        // Arrange
        await using var endpoint = new CommandEndpointListener();
        var client = new ResourceControlPlaneCommandClient();
        var command = new ResourceCommand(id, kind, owner, key, Encoding.UTF8.GetBytes(payloadJson));

        // Act
        Func<Task> apply = async () =>
            await client.ApplyAsync(endpoint.Address("/cohesion/v1"), token, command, null, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<ArgumentException>(apply);
        endpoint.Received.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command client: A null command fails before any request")]
    public async Task DeleteAsync_NullCommand_ShouldThrowArgumentNullException()
    {
        // Arrange
        await using var endpoint = new CommandEndpointListener();
        var client = new ResourceControlPlaneCommandClient();

        // Act
        Func<Task> delete = async () =>
            await client.DeleteAsync(endpoint.Address("/cohesion/v1"), token, null!, null, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<ArgumentNullException>(delete);
        endpoint.Received.IsCompleted.ShouldBeFalse();
    }

    private static ResourceCommand CreateCommand() =>
        new("cmd-1", "database.add-database", "appa", "orders", Encoding.UTF8.GetBytes(payloadJson));

    private static string ExpectedEnvelope() =>
        "{\"id\":\"cmd-1\",\"kind\":\"database.add-database\",\"owner\":\"appa\",\"key\":\"orders\",\"payload\":\"" +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)) + "\"}";

    private static X509Certificate2 CreateServerCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // SChannel cannot serve an ephemeral key; round-trip through PKCS#12 to get a usable one.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }
}
