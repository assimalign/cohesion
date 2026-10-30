using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// The gateway's default instance client: the control-plane HTTP binding as a caller sees it.
/// </summary>
public sealed class HttpApplicationInstanceClientTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - HttpApplicationInstanceClient: ";

    private static readonly Uri _endpoint = new("http://10.0.0.5:9090/");

    [Fact(DisplayName = DisplayPrefix + "A write posts its input to the action's route and returns the result with its state")]
    public async Task ExecuteAsync_WithWrite_ShouldPostTheInputToTheActionsRoute()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"status":"Succeeded","state":{"action":"log-level","value":{"Level":"Debug"}}}""");
        var client = new HttpApplicationInstanceClient(handler);

        // Act
        ApplicationActionResult result = await client.ExecuteAsync(Instance(_endpoint), TestManifests.LogLevel, ApplicationActionContext.Create(
            ApplicationActionKind.Write, TestSerializer.Json, """{"Level":"Debug"}"""u8.ToArray()));

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        result.State.ShouldNotBeNull().Action.ShouldBe(TestManifests.LogLevel);
        Encoding.UTF8.GetString(result.State.Value.Span).ShouldBe("""{"Level":"Debug"}""");
        (HttpMethod method, Uri uri, string? contentType, string body) = handler.Requests.ShouldHaveSingleItem();
        method.ShouldBe(HttpMethod.Post);
        uri.ShouldBe(new Uri("http://10.0.0.5:9090/actions/log-level"));
        contentType.ShouldBe("application/json");
        body.ShouldBe("""{"Level":"Debug"}""");
    }

    [Fact(DisplayName = DisplayPrefix + "A read gets the action's route without a body and returns its output")]
    public async Task ExecuteAsync_WithRead_ShouldGetTheActionsRoute()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"status":"Succeeded","output":{"Level":"Information"}}""");
        var client = new HttpApplicationInstanceClient(handler);

        // Act
        ApplicationActionResult result = await client.ExecuteAsync(Instance(_endpoint), TestManifests.LogLevel, ApplicationActionContext.Create(ApplicationActionKind.Read, TestSerializer.Json));

        // Assert
        Encoding.UTF8.GetString(result.Output.Span).ShouldBe("""{"Level":"Information"}""");
        (HttpMethod method, Uri uri, string? contentType, _) = handler.Requests.ShouldHaveSingleItem();
        method.ShouldBe(HttpMethod.Get);
        uri.ShouldBe(new Uri("http://10.0.0.5:9090/actions/log-level"));
        contentType.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "A restore puts the snapshot's value to the action's state route")]
    public async Task RestoreAsync_ShouldPutTheValueToTheStateRoute()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"status":"Succeeded"}""");
        var client = new HttpApplicationInstanceClient(handler);

        // Act
        ApplicationActionResult result = await client.RestoreAsync(Instance(_endpoint), TestManifests.LogLevelSnapshot("Warning"));

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        (HttpMethod method, Uri uri, string? contentType, string body) = handler.Requests.ShouldHaveSingleItem();
        method.ShouldBe(HttpMethod.Put);
        uri.ShouldBe(new Uri("http://10.0.0.5:9090/state/log-level"));
        contentType.ShouldBe("application/json");
        body.ShouldBe("""{"Level":"Warning"}""");
    }

    [Theory(DisplayName = DisplayPrefix + "A refusal the binding answers with a result is returned, not thrown")]
    [InlineData(HttpStatusCode.BadRequest, """{"status":"Rejected","reason":"The level is unknown."}""", ApplicationActionStatus.Rejected)]
    [InlineData(HttpStatusCode.NotFound, """{"status":"NotFound"}""", ApplicationActionStatus.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed, """{"status":"NotSupported"}""", ApplicationActionStatus.NotSupported)]
    public async Task ExecuteAsync_WithRefusal_ShouldReturnTheResult(HttpStatusCode status, string body, ApplicationActionStatus expected)
    {
        // Arrange
        var client = new HttpApplicationInstanceClient(new StubHttpMessageHandler(status, body));

        // Act
        ApplicationActionResult result = await client.ExecuteAsync(Instance(_endpoint), TestManifests.LogLevel, ApplicationActionContext.Create(ApplicationActionKind.Read, TestSerializer.Json));

        // Assert
        result.Status.ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "An answer without a result, or another status, is no answer")]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "<html></html>")]
    [InlineData(HttpStatusCode.InternalServerError, """{"status":"Succeeded"}""")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    public async Task ExecuteAsync_WithoutAResult_ShouldThrow(HttpStatusCode status, string body)
    {
        // Arrange
        var client = new HttpApplicationInstanceClient(new StubHttpMessageHandler(status, body));

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(
            () => client.ExecuteAsync(Instance(_endpoint), TestManifests.LogLevel, ApplicationActionContext.Create(ApplicationActionKind.Read, TestSerializer.Json)).AsTask());

        // Assert
        exception.StatusCode.ShouldBe(status);
    }

    [Theory(DisplayName = DisplayPrefix + "An instance with no control plane, or one the client cannot call, is refused before any request")]
    [InlineData(null)]
    [InlineData("inprocess://local/")]
    [InlineData("tcp://10.0.0.5:9090/")]
    public async Task ExecuteAsync_WithEndpointItCannotCall_ShouldThrowArgumentException(string? endpoint)
    {
        // Arrange
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"status":"Succeeded"}""");
        var client = new HttpApplicationInstanceClient(handler);

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(
            () => client.ExecuteAsync(Instance(endpoint is null ? null : new Uri(endpoint)), TestManifests.LogLevel, ApplicationActionContext.Create(ApplicationActionKind.Read, TestSerializer.Json)).AsTask());
        handler.Requests.ShouldBeEmpty();
    }

    private static ApplicationInstanceManifest Instance(Uri? endpoint)
    {
        return TestManifests.Instance() with { ControlPlaneEndpoint = endpoint };
    }
}
