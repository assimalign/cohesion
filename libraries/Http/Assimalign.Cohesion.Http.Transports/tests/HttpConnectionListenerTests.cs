using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Transports.Tests;

public class HttpConnectionListenerTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should accept and adapt an HTTP/1.1 transport connection")]
    public async Task AcceptOrListenAsync_OnQueuedHttp1Connection_ShouldAdaptConnection()
    {
        // Arrange
        TestConnection connection = new(HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        TestConnectionListener transportListener = new(connection);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(transportListener);

        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpConnection httpConnection = await listener.AcceptOrListenAsync();
        IHttpConnectionContext httpConnectionContext = await httpConnection.OpenAsync();

        // Assert — the HTTP connection projects the transport connection's identity.
        httpConnection.Id.ShouldBe(connection.Id);
        httpConnectionContext.LocalEndPoint.ShouldBe(connection.LocalEndPoint);
        httpConnectionContext.RemoteEndPoint.ShouldBe(connection.RemoteEndPoint);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should re-arm transport accepts and backlog connections")]
    public async Task AcceptOrListenAsync_OnSequentialAccepts_ShouldBacklogConnections()
    {
        // Arrange
        TestConnection firstConnection = new(HttpProtocolPayloadFactory.CreateHttp1Request("GET /first HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        TestConnection secondConnection = new(HttpProtocolPayloadFactory.CreateHttp1Request("GET /second HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        TestConnectionListener transportListener = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(transportListener);
        transportListener.Enqueue(firstConnection);

        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpConnection acceptedFirstConnection = await listener.AcceptOrListenAsync();
        await transportListener.WaitingForConnection.WaitAsync(TimeSpan.FromSeconds(1));
        transportListener.Enqueue(secondConnection);
        IHttpConnection acceptedSecondConnection = await listener.AcceptOrListenAsync().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert
        acceptedFirstConnection.Id.ShouldBe(firstConnection.Id);
        acceptedSecondConnection.Id.ShouldBe(secondConnection.Id);
        transportListener.AcceptCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should map an HTTP/1.1 registration to HTTP/1.1 connections")]
    public async Task AcceptOrListenAsync_OnHttp1Registration_ShouldYieldHttp11Contexts()
    {
        // Arrange
        TestConnection connection = new(HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert — the registration's protocol, not the payload, selects the parser.
        httpContext.Version.ShouldBe(HttpVersion.Http11);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should map an HTTP/2 registration to HTTP/2 connections")]
    public async Task AcceptOrListenAsync_OnHttp2Registration_ShouldYieldHttp20Contexts()
    {
        // Arrange
        TestConnection connection = new(HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert
        httpContext.Version.ShouldBe(HttpVersion.Http20);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should aggregate the configured protocols")]
    public void Protocols_OnMultipleRegistrations_ShouldAggregateConfiguredProtocols()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options
            .UseHttp1(new TestConnectionListener())
            .UseHttp2(new TestConnectionListener())
            .UseHttp3(new TestMultiplexedConnectionListener());

        // Act
        HttpConnectionListener listener = new(options);

        // Assert
        listener.Protocols.ShouldBe(HttpProtocol.Http11 | HttpProtocol.Http20 | HttpProtocol.Http30);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should reject accepting when no listener is configured")]
    public async Task AcceptOrListenAsync_OnNoConfiguredListeners_ShouldThrowInvalidOperationException()
    {
        // Arrange
        await using HttpConnectionListener listener = new(new HttpConnectionListenerOptions());

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(() => listener.AcceptOrListenAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should throw ObjectDisposedException for accepts after disposal")]
    public async Task AcceptOrListenAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener());
        HttpConnectionListener listener = new(options);

        // Act
        await listener.DisposeAsync();

        // Assert
        await Should.ThrowAsync<ObjectDisposedException>(() => listener.AcceptOrListenAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should fault a pending accept with ObjectDisposedException on disposal")]
    public async Task AcceptOrListenAsync_OnDisposeWhilePending_ShouldThrowObjectDisposedException()
    {
        // Arrange — no queued connections, so the accept parks on the backlog.
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener());
        HttpConnectionListener listener = new(options);

        Task<HttpConnection> pendingAccept = listener.AcceptOrListenAsync();

        // Act
        await listener.DisposeAsync();

        // Assert
        await Should.ThrowAsync<ObjectDisposedException>(() => pendingAccept.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should observe caller cancellation on a pending accept")]
    public async Task AcceptOrListenAsync_OnCallerCancellation_ShouldThrowOperationCanceledException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener());
        await using HttpConnectionListener listener = new(options);
        using CancellationTokenSource cancellationTokenSource = new();

        Task<HttpConnection> pendingAccept = listener.AcceptOrListenAsync(cancellationTokenSource.Token);

        // Act
        cancellationTokenSource.Cancel();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => pendingAccept.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should dispose the registered connection listeners on disposal")]
    public async Task DisposeAsync_OnRegisteredListeners_ShouldDisposeEveryListener()
    {
        // Arrange
        TestConnectionListener streamListener = new();
        TestMultiplexedConnectionListener multiplexedListener = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(streamListener);
        options.UseHttp3(multiplexedListener);
        HttpConnectionListener listener = new(options);

        // Act
        await listener.DisposeAsync();

        // Assert
        streamListener.IsDisposed.ShouldBeTrue();
        multiplexedListener.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should surface an accept-loop failure to the caller")]
    public async Task AcceptOrListenAsync_OnAcceptLoopFailure_ShouldRethrowListenerException()
    {
        // Arrange — the transport listener fails its accept outright; the
        // accept loop completes the backlog channel with that exception and
        // the next AcceptOrListenAsync surfaces it instead of hanging.
        InvalidOperationException failure = new("listener exploded");
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new FailingConnectionListener(failure));

        await using HttpConnectionListener listener = new(options);

        // Act + Assert
        InvalidOperationException observed = await Should.ThrowAsync<InvalidOperationException>(
            () => listener.AcceptOrListenAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        observed.Message.ShouldBe(failure.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Create should reject a null configuration callback")]
    public void Create_OnNullConfigure_ShouldThrowArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => HttpConnectionListener.Create(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Constructor should reject null options")]
    public void Constructor_OnNullOptions_ShouldThrowArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new HttpConnectionListener(null!));
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    /// <summary>
    /// A listener whose accept always faults, for exercising the accept
    /// loop's failure propagation path.
    /// </summary>
    private sealed class FailingConnectionListener : ConnectionListener
    {
        private readonly Exception _exception;

        public FailingConnectionListener(Exception exception)
        {
            _exception = exception;
        }

        public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 17000);

        public override ConnectionCapabilities Capabilities => TestConnection.DefaultCapabilities;

        public override ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<Connection>(_exception);

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
