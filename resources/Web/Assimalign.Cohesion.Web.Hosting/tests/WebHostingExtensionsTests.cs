using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http.Connections;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Covers the <see cref="WebHostingExtensions"/> convenience surface that the deleted
/// <c>Assimalign.Cohesion.Web.Server</c> project never had tests for (issue #766). The wrapper is
/// the single home for the <c>UseHttp1</c>/<c>UseHttp2</c> callback sugar over the TCP driver.
/// </summary>
public class WebHostingExtensionsTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp1: Should throw when the configure callback is null")]
    public void UseHttp1_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp1((Action<TcpConnectionListenerOptions>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp2: Should throw when the configure callback is null")]
    public void UseHttp2_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp2((Action<TcpConnectionListenerOptions>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp1: Should defer TCP listener creation and register HTTP/1.1")]
    public async Task UseHttp1_WithConfiguredOptions_ShouldDeferCreationAndRegisterHttp11()
    {
        // Arrange
        bool configured = false;
        HttpConnectionListenerOptions options = new();

        // Act
        HttpConnectionListenerOptions result = options.UseHttp1(tcp =>
        {
            configured = true;
            tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        });

        // Assert
        // The wrapper returns the same options for fluent chaining and defers construction:
        // the deferred-factory form does not build the TCP listener until the
        // HttpConnectionListener materializes the registration.
        result.ShouldBeSameAs(options);
        configured.ShouldBeFalse();

        await using HttpConnectionListener listener = new(options);

        configured.ShouldBeTrue();
        listener.Protocols.ShouldBe(HttpProtocol.Http11);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp2: Should defer TCP listener creation and register HTTP/2")]
    public async Task UseHttp2_WithConfiguredOptions_ShouldDeferCreationAndRegisterHttp20()
    {
        // Arrange
        bool configured = false;
        HttpConnectionListenerOptions options = new();

        // Act
        HttpConnectionListenerOptions result = options.UseHttp2(tcp =>
        {
            configured = true;
            tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        });

        // Assert
        result.ShouldBeSameAs(options);
        configured.ShouldBeFalse();

        await using HttpConnectionListener listener = new(options);

        configured.ShouldBeTrue();
        listener.Protocols.ShouldBe(HttpProtocol.Http20);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp1: Should produce a listener that accepts HTTP/1.1 connections")]
    public async Task UseHttp1_WithConfiguredOptions_ShouldProduceListenerThatAcceptsConnections()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        IPEndPoint endPoint = new(IPAddress.Loopback, GetAvailableLoopbackPort());

        HttpConnectionListenerOptions options = new();
        options.UseHttp1(tcp => tcp.EndPoint = endPoint);

        await using HttpConnectionListener listener = new(options);

        // Act
        // The accept loop binds the wrapped TCP listener lazily on its first accept, so the
        // client connect is retried until the loop is listening on the configured endpoint.
        Task<HttpConnection> acceptTask = listener.AcceptOrListenAsync(cancellation.Token);

        using Socket client = await ConnectWithRetryAsync(endPoint, cancellation.Token);
        await using HttpConnection accepted = await acceptTask;

        // Assert
        accepted.ShouldNotBeNull();
        listener.Protocols.ShouldBe(HttpProtocol.Http11);
    }

    private static int GetAvailableLoopbackPort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static async Task<Socket> ConnectWithRetryAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await client.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);

                return client;
            }
            catch (SocketException)
            {
                // The listener has not bound yet; dispose this attempt and retry until the
                // shared test timeout cancels the token.
                client.Dispose();

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
