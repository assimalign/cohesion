using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;

using DnsEndPoint = System.Net.DnsEndPoint;
using IPAddress = System.Net.IPAddress;
using IPEndPoint = System.Net.IPEndPoint;

using Assimalign.Cohesion.Web.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class ResourceControlPlaneStandaloneTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Standalone: refuses remote namespaced routes before dispatch")]
    [InlineData("/cohesion/v1", "GET")]
    [InlineData("/cohesion/v1/future", "GET")]
    [InlineData("/cohesion/v1/healthz", "GET")]
    [InlineData("/cohesion/v1/readyz", "HEAD")]
    [InlineData("/cohesion/v1/livez", "GET")]
    [InlineData("/cohesion/v1/endpoints", "GET")]
    [InlineData("/cohesion/v1/commands", "GET")]
    [InlineData("/cohesion/v1/commands", "POST")]
    [InlineData("/cohesion/v1/commands", "DELETE")]
    [InlineData("/cohesion/v1/stop", "POST")]
    public async Task InvokeAsync_StandaloneRemoteRoute_ShouldRefuseBeforeDispatch(string path, string method)
    {
        // Arrange
        var plane = new RecordingControlPlane();
        var completion = new RecordingCompletionFeature();
        await using var context = new ControlPlaneExchange(path, new HttpMethod(method));
        context.Connection.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 12345);
        context.Features.Set(completion);

        // Act
        await ResourceControlPlaneMiddleware.InvokeAsync(plane, new ResourceContext(), true, null, context,
            _ => throw new InvalidOperationException("Control-plane requests must be terminal."));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        context.Response.Body.Length.ShouldBe(0);
        context.Response.Headers.ContainsKey(HttpHeaderKey.WWWAuthenticate).ShouldBeFalse();
        completion.Callbacks.ShouldBeEmpty();
        plane.StopTokens.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Standalone: rejects spoofed forwarding and untrusted peers")]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:192.0.2.10")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.2")]
    [InlineData("::ffff:127.0.0.2")]
    [InlineData(null)]
    [InlineData("localhost")]
    public async Task InvokeAsync_StandaloneUntrustedPeerWithForwardedHeaders_ShouldRefuse(string? peer)
    {
        // Arrange
        var plane = new RecordingControlPlane();
        await using var context = new ControlPlaneExchange("/cohesion/v1/stop", HttpMethod.Post);
        context.Connection.RemoteEndPoint = peer is null ? null : peer == "localhost"
            ? new DnsEndPoint(peer, 12345)
            : new IPEndPoint(IPAddress.Parse(peer), 12345);
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        context.Request.Headers["Forwarded"] = "for=\"[::1]\"";

        // Act
        await ResourceControlPlaneMiddleware.InvokeAsync(plane, new ResourceContext(), true, null, context,
            _ => throw new InvalidOperationException("Stop must be terminal."));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        plane.StopTokens.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Standalone: accepts loopback transport peers")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task InvokeAsync_StandaloneLoopbackPeer_ShouldAccept(string peer)
    {
        // Arrange
        var plane = new RecordingControlPlane();
        await using var context = new ControlPlaneExchange("/cohesion/v1/stop", HttpMethod.Post);
        context.Connection.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(peer), 12345);
        context.Request.Headers["X-Forwarded-For"] = "192.0.2.10";

        // Act
        await ResourceControlPlaneMiddleware.InvokeAsync(plane, new ResourceContext(), true, null, context,
            _ => throw new InvalidOperationException("Stop must be terminal."));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        plane.StopTokens.ShouldBe(new[] { CancellationToken.None });
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Standalone: bare probes stay public")]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    [InlineData("/livez")]
    public async Task InvokeAsync_StandaloneRemoteBareProbe_ShouldAccept(string path)
    {
        // Arrange
        var plane = new RecordingControlPlane();
        await using var context = new ControlPlaneExchange(path, HttpMethod.Get);
        context.Connection.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 12345);

        // Act
        await ResourceControlPlaneMiddleware.InvokeAsync(plane, new ResourceContext(), true, null, context,
            _ => throw new InvalidOperationException("Probes must be terminal."));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Managed: remote callers still require gateway credentials")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_ManagedRemotePeer_ShouldPreserveCredentialCheck(bool credentialed)
    {
        // Arrange
        var plane = new RecordingControlPlane();
        using var identity = new TestBootstrapIdentity("tests", "gateway");
        var resource = new ResourceContext("tests", "resource", "Testing", "gateway", contentRootPath: null,
            endpoints: null, mounts: null, settings: null, references: null, bootstrapCredential: default,
            applicationTrustKey: identity.PublicKey, ambientValues: null);
        await using var context = new ControlPlaneExchange("/cohesion/v1/stop", HttpMethod.Post);
        context.Connection.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 12345);
        if (credentialed)
        {
            context.Request.Headers[HttpHeaderKey.Authorization] = "Bearer " + identity.Issue("resource");
        }

        // Act
        await ResourceControlPlaneMiddleware.InvokeAsync(plane, resource, true, null, context,
            _ => throw new InvalidOperationException("Stop must be terminal."));

        // Assert
        context.Response.StatusCode.ShouldBe(credentialed ? HttpStatusCode.Accepted : HttpStatusCode.Unauthorized);
        plane.StopTokens.Count.ShouldBe(credentialed ? 1 : 0);
    }
}
