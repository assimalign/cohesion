using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.LogSpace.Hosting.Internal;

using DnsEndPoint = System.Net.DnsEndPoint;
using IPAddress = System.Net.IPAddress;
using IPEndPoint = System.Net.IPEndPoint;

namespace Assimalign.Cohesion.LogSpace.Hosting.Tests;

public sealed class LogSpaceHttpTests
{
    [Theory(DisplayName = "Cohesion Test [LogSpace.Hosting] - Standalone query gate refuses remote callers on every namespaced route")]
    [InlineData("/cohesion/v1")]
    [InlineData("/cohesion/v1/logs")]
    [InlineData("/cohesion/v1/endpoints")]
    [InlineData("/cohesion/v1/commands")]
    [InlineData("/cohesion/v1/stop")]
    [InlineData("/cohesion/v1/healthz")]
    [InlineData("/cohesion/v1/readyz")]
    [InlineData("/cohesion/v1/livez")]
    public async Task QueryAsync_StandaloneRemotePeer_ShouldRefuseNamespacedRoutes(string path)
    {
        // Arrange
        await using var exchange = CreateExchange(path, "192.0.2.1");
        bool nextCalled = false;

        // Act
        await LogSpaceHttp.QueryAsync(exchange, _ => { nextCalled = true; return Task.CompletedTask; }, store: null);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        exchange.Response.Body.Length.ShouldBe(0);
        nextCalled.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [LogSpace.Hosting] - Standalone query accepts the explicit loopback peers")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task QueryAsync_StandaloneLoopbackPeer_ShouldServeQuery(string address)
    {
        // Arrange
        await using var exchange = CreateExchange("/cohesion/v1/logs", address);

        // Act
        await LogSpaceHttp.QueryAsync(exchange, _ => Task.CompletedTask, store: null);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        exchange.Response.Headers[HttpHeaderKey.ContentType].ToString().ShouldBe("application/x-ndjson");
    }

    [Theory(DisplayName = "Cohesion Test [LogSpace.Hosting] - Forwarded loopback values cannot spoof a standalone query peer")]
    [InlineData("192.0.2.1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:192.0.2.1")]
    [InlineData("127.0.0.2")]
    public async Task QueryAsync_StandaloneForwardedLoopback_ShouldRefuseRemotePeer(string address)
    {
        // Arrange
        await using var exchange = CreateExchange("/cohesion/v1/logs", address);
        exchange.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        exchange.Request.Headers["Forwarded"] = "for=127.0.0.1";

        // Act
        await LogSpaceHttp.QueryAsync(exchange, _ => Task.CompletedTask, store: null);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory(DisplayName = "Cohesion Test [LogSpace.Hosting] - Standalone query fails closed without an IP transport endpoint")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_StandaloneUnknownPeer_ShouldRefuseQuery(bool dnsEndpoint)
    {
        // Arrange
        await using var exchange = CreateExchange("/cohesion/v1/logs", "127.0.0.1");
        exchange.Connection.RemoteEndPoint = dnsEndpoint ? new DnsEndPoint("localhost", 12345) : null;

        // Act
        await LogSpaceHttp.QueryAsync(exchange, _ => Task.CompletedTask, store: null);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory(DisplayName = "Cohesion Test [LogSpace.Hosting] - Bare probes remain outside the standalone query gate")]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    [InlineData("/livez")]
    public async Task QueryAsync_StandaloneRemoteProbe_ShouldForwardToNextMiddleware(string path)
    {
        // Arrange
        await using var exchange = CreateExchange(path, "192.0.2.1");
        bool nextCalled = false;

        // Act
        await LogSpaceHttp.QueryAsync(exchange, _ => { nextCalled = true; return Task.CompletedTask; }, store: null);

        // Assert
        nextCalled.ShouldBeTrue();
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Theory(DisplayName = "Cohesion Test [LogSpace.Hosting] - A bearer credential no longer admits a remote query peer")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_RemotePeerWithOrWithoutCredential_ShouldRefuseQuery(bool credential)
    {
        // Arrange
        await using var exchange = CreateExchange("/cohesion/v1/logs", "192.0.2.1");
        if (credential)
        {
            exchange.Request.Headers[HttpHeaderKey.Authorization] = "Bearer eyJhbGciOiJFUzI1NiJ9.e30.c2ln";
        }

        // Act
        await LogSpaceHttp.QueryAsync(exchange, _ => Task.CompletedTask, store: null);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory(DisplayName = "Cohesion Test [LogSpace.Hosting] - OTLP ingest fails closed for every peer")]
    [InlineData("192.0.2.1")]
    [InlineData("127.0.0.1")]
    public async Task IngestAsync_AnyPeer_ShouldRefuseTelemetry(string address)
    {
        // Arrange
        await using var exchange = new ControlPlaneExchange("/v1/traces", HttpMethod.Post)
        {
            RequestCancelled = CancellationToken.None,
        };
        exchange.Connection.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), 12345);
        exchange.Request.Headers[HttpHeaderKey.ContentType] = "application/json";
        exchange.Request.Headers[HttpHeaderKey.Authorization] = "Bearer eyJhbGciOiJFUzI1NiJ9.e30.c2ln";
        var store = new LogSegmentStore(Path.GetTempPath());

        // Act
        await LogSpaceHttp.IngestAsync(exchange, store);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        exchange.Response.Body.Length.ShouldBe(0);
    }

    private static ControlPlaneExchange CreateExchange(string path, string address)
    {
        var exchange = new ControlPlaneExchange(path, HttpMethod.Get) { RequestCancelled = CancellationToken.None };
        exchange.Connection.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), 12345);
        return exchange;
    }
}
