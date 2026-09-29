using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

public sealed partial class GatewayControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Routing: Should return 404 for paths no /cohesion/v1 template matches")]
    public async Task Route_UnknownPath_ShouldReturnNotFound()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        (IApplicationGateway control, _, Uri address) = await StartRoutingGatewayAsync(root, cancellation.Token);
        using var client = new HttpClient();

        try
        {
            // Act
            using HttpResponseMessage unknown = await client.GetAsync(
                new Uri(address, "/cohesion/v1/unknown"), cancellation.Token);
            using HttpResponseMessage prefix = await client.GetAsync(
                new Uri(address, "/cohesion/v1"), cancellation.Token);
            using HttpResponseMessage tooLong = await client.GetAsync(
                new Uri(address, "/cohesion/v1/resources/api/commands/id/extra"), cancellation.Token);
            using HttpResponseMessage wrongLiteral = await client.GetAsync(
                new Uri(address, "/cohesion/v2/application"), cancellation.Token);

            // Assert
            unknown.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
            prefix.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
            tooLong.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
            wrongLiteral.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Routing: Should return 405 with the acceptable methods in route order")]
    public async Task Route_UnacceptedMethod_ShouldReturnMethodNotAllowedWithAllow()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        (IApplicationGateway control, _, Uri address) = await StartRoutingGatewayAsync(root, cancellation.Token);
        using var client = new HttpClient();

        try
        {
            // Act
            using HttpResponseMessage postApplication = await SendUnauthenticatedAsync(
                client, HttpMethod.Post, new Uri(address, "/cohesion/v1/application"), cancellation.Token);
            using HttpResponseMessage putResource = await SendUnauthenticatedAsync(
                client, HttpMethod.Put, new Uri(address, "/cohesion/v1/resources/api"), cancellation.Token);
            using HttpResponseMessage deleteCommands = await SendUnauthenticatedAsync(
                client, HttpMethod.Delete, new Uri(address, "/cohesion/v1/resources/api/commands"), cancellation.Token);
            using HttpResponseMessage getCommand = await SendUnauthenticatedAsync(
                client, HttpMethod.Get, new Uri(address, "/cohesion/v1/resources/api/commands/id"), cancellation.Token);

            // Assert
            postApplication.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
            string.Join(", ", postApplication.Content.Headers.Allow).ShouldBe("GET, HEAD");
            putResource.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
            string.Join(", ", putResource.Content.Headers.Allow).ShouldBe("GET, HEAD");
            deleteCommands.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
            string.Join(", ", deleteCommands.Content.Headers.Allow).ShouldBe("GET, HEAD");
            getCommand.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
            string.Join(", ", getCommand.Content.Headers.Allow).ShouldBe("PUT, DELETE");
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Routing: Should serve HEAD through the GET handler")]
    public async Task Route_HeadOnGetRoute_ShouldInvokeTheGetHandler()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        (IApplicationGateway control, _, Uri address) = await StartRoutingGatewayAsync(root, cancellation.Token);
        using var client = new HttpClient();

        try
        {
            // Act: the GET handler authenticates first, so an anonymous HEAD proves it ran.
            using HttpResponseMessage head = await SendUnauthenticatedAsync(
                client, HttpMethod.Head, new Uri(address, "/cohesion/v1/application"), cancellation.Token);

            // Assert
            head.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Routing: Should match literals case-insensitively and pass parameter values verbatim")]
    public async Task Route_LiteralCaseAndParameterValue_ShouldMatchLiteralsAndPreserveValues()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        (IApplicationGateway control, IApplicationModel model, Uri address) = await StartRoutingGatewayAsync(
            root, cancellation.Token);
        using var client = new HttpClient();

        try
        {
            string token = await ((IApplicationTrustGateway)control).IssueDeveloperTokenAsync(
                model, "developer", cancellation.Token);

            // Act
            using HttpResponseMessage upperLiterals = await SendAsync(
                client, HttpMethod.Get, new Uri(address, "/COHESION/V1/RESOURCES/api"), token,
                content: null, cancellation.Token);
            using HttpResponseMessage upperValue = await SendAsync(
                client, HttpMethod.Get, new Uri(address, "/cohesion/v1/resources/API"), token,
                content: null, cancellation.Token);

            // Assert
            upperLiterals.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            (await upperLiterals.Content.ReadAsStringAsync(cancellation.Token))
                .ShouldContain("\"name\": \"api\"", Case.Sensitive);
            upperValue.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
            using JsonDocument error = JsonDocument.Parse(
                await upperValue.Content.ReadAsStringAsync(cancellation.Token));
            error.RootElement.GetProperty("error").GetString()
                .ShouldBe("Application 'appa' has no resource named 'API'.");
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    private static async Task<(IApplicationGateway Control, IApplicationModel Model, Uri Address)> StartRoutingGatewayAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = root,
            ControlPlane = GatewayControlPlane.CreateFactory(controlPlane =>
                controlPlane.MetadataDirectory = root),
        };
        var gateway = new TestGateway(options);
        IApplicationModel model = BuildModel(gateway, "appa", includeUnsupportedResource: false);
        IApplicationGateway control = gateway;
        await control.StartAsync(model, cancellationToken);
        (Uri address, _) = ReadMetadata(root, "appa");
        return (control, model, address);
    }

    private static async Task<HttpResponseMessage> SendUnauthenticatedAsync(
        HttpClient client,
        HttpMethod method,
        Uri address,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, address);
        return await client.SendAsync(request, cancellationToken);
    }
}
