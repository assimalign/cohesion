using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.WebSockets.Internal;

/// <summary>
/// The socket an application receives from an accept under <c>UseWebSockets</c> when the server
/// publishes its drain signal (<see cref="IWebServerDrainFeature"/>). It forwards everything to the
/// BCL socket, and when the drain begins it starts the close handshake with
/// <c>1001 Going Away</c>.
/// </summary>
/// <remarks>
/// <para>
/// Sending the close behind the application's back would otherwise break its own close: once the
/// peer answers, the BCL socket is <see cref="WebSocketState.Closed"/>, and the
/// <see cref="CloseAsync"/> or <see cref="CloseOutputAsync"/> that ends a typical receive loop
/// throws for an invalid state. So the close output is claimed once, by whichever side asks first.
/// When the drain claimed it, an application's <see cref="CloseOutputAsync"/> waits for the drain's
/// close frame to be sent, and its <see cref="CloseAsync"/> also waits for the peer's answer; when
/// the application claimed it first, the drain does nothing.
/// </para>
/// <para>
/// After the drain's close frame is sent, a send fails as it does on any socket whose close was
/// sent, and a receive returns the rest of the peer's messages and then its close.
/// </para>
/// </remarks>
internal sealed class DrainAwareWebSocket : WebSocket
{
    private const string drainDescription = "The server is shutting down.";

    private const int noOwner = 0;
    private const int applicationOwner = 1;
    private const int drainOwner = 2;

    private readonly WebSocket _inner;
    private readonly TaskCompletionSource _drainCloseSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _drainRegistration;
    private int _closeOwner;

    public DrainAwareWebSocket(WebSocket inner, CancellationToken draining)
    {
        _inner = inner;

        // A drain that already began runs the callback here, before the registration is assigned;
        // the callback reads nothing the constructor has not set by then.
        _drainRegistration = draining.UnsafeRegister(static state => ((DrainAwareWebSocket)state!).OnDraining(), this);
    }

    /// <inheritdoc />
    public override WebSocketCloseStatus? CloseStatus => _inner.CloseStatus;

    /// <inheritdoc />
    public override string? CloseStatusDescription => _inner.CloseStatusDescription;

    /// <inheritdoc />
    public override WebSocketState State => _inner.State;

    /// <inheritdoc />
    public override string? SubProtocol => _inner.SubProtocol;

    /// <summary>
    /// Stops watching the drain. Called when the exchange that accepted the socket completes: the
    /// connection ends with it, so there is nothing left to close.
    /// </summary>
    public void ReleaseDrain()
    {
        _drainRegistration.Unregister();
    }

    /// <inheritdoc />
    public override void Abort()
    {
        ReleaseDrain();
        _inner.Abort();
    }

    /// <inheritdoc />
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        return ClaimCloseOutput()
            ? _inner.CloseAsync(closeStatus, statusDescription, cancellationToken)
            : CompleteDrainCloseAsync(closeStatus, statusDescription, cancellationToken);
    }

    /// <inheritdoc />
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        return ClaimCloseOutput()
            ? _inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken)
            : _drainCloseSent.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        ReleaseDrain();
        _inner.Dispose();
    }

    /// <inheritdoc />
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        return _inner.ReceiveAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        return _inner.ReceiveAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        return _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
    }

    /// <inheritdoc />
    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        return _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
    }

    /// <inheritdoc />
    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken = default)
    {
        return _inner.SendAsync(buffer, messageType, messageFlags, cancellationToken);
    }

    /// <summary>
    /// Claims the close output for the application. Returns <see langword="false"/> when the drain
    /// claimed it first; an application that claims it twice is passed through to the BCL socket,
    /// which reports the misuse as it always does.
    /// </summary>
    private bool ClaimCloseOutput()
    {
        return Interlocked.CompareExchange(ref _closeOwner, applicationOwner, noOwner) != drainOwner;
    }

    private void OnDraining()
    {
        if (Interlocked.CompareExchange(ref _closeOwner, drainOwner, noOwner) != noOwner)
        {
            return;
        }

        // Started from the drain's cancellation callback, which runs on the thread that stops the
        // server: only the start of the close frame's write runs here.
        _ = SendDrainCloseAsync();
    }

    private async Task SendDrainCloseAsync()
    {
        try
        {
            await _inner.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, drainDescription, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The socket was already failing, aborted or disposed: there is no close to send, and the
            // application learns of the failure from its own next call.
        }
        finally
        {
            _drainCloseSent.TrySetResult();
        }
    }

    private async Task CompleteDrainCloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        await _drainCloseSent.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // The drain's close frame is out. Unless the peer's answer already arrived, finish the
        // handshake the way CloseAsync does: wait for it.
        if (_inner.State == WebSocketState.CloseSent)
        {
            await _inner.CloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
        }
    }
}
