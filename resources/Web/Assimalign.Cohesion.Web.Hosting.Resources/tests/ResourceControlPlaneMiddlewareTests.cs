using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

public sealed class ResourceControlPlaneMiddlewareTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting.Resources] - Stop: waits for response completion")]
    public async Task InvokeAsync_WithCompletionFeature_ShouldDeferStop()
    {
        // Arrange
        var plane = new RecordingControlPlane();
        var completion = new RecordingCompletionFeature();
        using var cancellation = new CancellationTokenSource();
        await using var context = new ControlPlaneExchange("/cohesion/v1/stop", HttpMethod.Post)
        {
            RequestCancelled = cancellation.Token,
        };
        context.Features.Set(completion);

        // Act
        await ResourceControlPlaneMiddleware.InvokeAsync(plane, new ResourceContext(), true, null, context,
            _ => throw new InvalidOperationException("Stop must be terminal."));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        completion.Callbacks.Count.ShouldBe(1);
        plane.StopTokens.ShouldBeEmpty();
        cancellation.Cancel();
        await completion.Callbacks[0]();
        plane.StopTokens.ShouldBe(new[] { CancellationToken.None });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting.Resources] - Stop: custom server without completion uses direct fallback")]
    public async Task InvokeAsync_WithoutCompletionFeature_ShouldStopDirectly()
    {
        // Arrange
        var plane = new RecordingControlPlane();
        using var cancellation = new CancellationTokenSource();
        await using var context = new ControlPlaneExchange("/cohesion/v1/stop", HttpMethod.Post)
        {
            RequestCancelled = cancellation.Token,
        };

        // Act
        await ResourceControlPlaneMiddleware.InvokeAsync(plane, new ResourceContext(), true, null, context,
            _ => throw new InvalidOperationException("Stop must be terminal."));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        plane.StopTokens.ShouldBe(new[] { cancellation.Token });
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting.Resources] - Port: gates bare probes and permits an absent gate")]
    [InlineData(8080, 8081, true)]
    [InlineData(8080, 8080, false)]
    [InlineData(null, 8081, false)]
    public async Task InvokeAsync_WithPortGate_ShouldServeOnlyMatchingListener(int? gate, int localPort, bool forwarded)
    {
        // Arrange
        var plane = new RecordingControlPlane();
        await using var context = new ControlPlaneExchange("/readyz", HttpMethod.Get);
        context.Connection.LocalPort = localPort;
        int nextCalls = 0;

        // Act
        await ResourceControlPlaneMiddleware.InvokeAsync(plane, new ResourceContext(), true, gate, context,
            _ => { nextCalls++; return Task.CompletedTask; });

        // Assert
        nextCalls.ShouldBe(forwarded ? 1 : 0);
        if (!forwarded)
        {
            context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
            context.Response.Headers.ContainsKey(HttpHeaderKey.ContentType).ShouldBeTrue();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting.Resources] - Validate: pins managed identity failures")]
    [InlineData("resource", "A gateway-managed resource requires a public application trust key.")]
    [InlineData(" ", "A gateway-managed resource requires an ambient resource name.")]
    public void Validate_WithIncompleteManagedIdentity_ShouldFailEagerly(string name, string message)
    {
        // Arrange
        var context = new ResourceContext(applicationName: "tests", resourceName: name, gatewayName: "gateway");

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ResourceControlPlaneMiddleware.Validate(context));

        // Assert
        error.Message.ShouldBe(message);
    }
}
