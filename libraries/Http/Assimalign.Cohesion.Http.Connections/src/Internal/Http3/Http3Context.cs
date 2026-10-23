using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

[global::System.Diagnostics.DebuggerDisplay("Http3Context")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class Http3Context : TransportHttpContext
{
    // 0 while the exchange runs, 1 once it has ended (SendAsync returned or threw, or the exchange was
    // disposed). A SendAsync that refused the head or a buffered trailer section (#1183) does not end it:
    // nothing reached the wire, and the exchange runs until the caller finalizes it again or disposes it.
    // Interlocked: the send path and the host's disposal race to end it.
    private int _exchangeEnded;
    private readonly CancellationTokenRegistration _streamClosedRegistration;

    // Automatic 100, application interims and the final head share one writer (#1385, #1428).
    // Only the active interim owns a completion source; queued writers cannot replace it.
    private readonly Lock _responseHeadLock = new();
    private bool _finalHeadClaimed;
    private TaskCompletionSource? _interimWritten;

    /// <summary>
    /// Initializes the exchange for a decoded request head.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpContext.RequestCancelled"/> fires on <paramref name="requestAborted"/> and on the
    /// request stream's <see cref="IMultiplexedConnectionStream.ConnectionClosed"/>, which the drivers signal when the
    /// client resets the request stream (<c>RESET_STREAM</c>), stops the response
    /// (<c>STOP_SENDING</c>), or the stream is aborted or lost. RFC 9114 §4.1.1 — that is how a client
    /// cancels a request, so the application learns of it as an HTTP/2 <c>RST_STREAM</c> tells it,
    /// without having to read or write (#1329). The multiplexed connection's closure also cancels
    /// the exchange even if the driver has not yet signaled the request stream (#1430). Once the
    /// complete response is flushed, its FIN and local input cleanup end the stream without
    /// cancelling the successfully answered exchange.
    /// </remarks>
    public Http3Context(
        in TransportHttpRequestHead requestHead,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        IMultiplexedConnectionStream streamConnection,
        long streamId,
        Http3RequestBodyStream requestBody,
        int featureCapacity,
        IHttpFeatureCollection? features = null,
        CancellationToken connectionAborted = default)
        : base(HttpVersion.Http30, requestHead, connectionInfo, requestAborted, featureCapacity, features, connectionAborted: connectionAborted)
    {
        StreamConnection = streamConnection;
        StreamId = streamId;
        RequestBody = requestBody;
        ExtendedConnectProtocol = requestHead.Protocol;
        _streamClosedRegistration = streamConnection.ConnectionClosed.UnsafeRegister(static state =>
        {
            Http3Context context = (Http3Context)state!;
            _ = context.TripRequestCancelled();
        }, this);
    }

    /// <summary>
    /// The <c>:protocol</c> of a valid extended CONNECT (RFC 9220), or <see langword="null"/> for any
    /// other request. Only an exchange that carries one can accept a tunnel
    /// (<see cref="Http3ExchangeControl.AcceptTunnelAsync"/>).
    /// </summary>
    public string? ExtendedConnectProtocol { get; }

    /// <summary>
    /// The bidirectional QUIC stream this exchange arrived on; the response is written
    /// back to its output.
    /// </summary>
    public IMultiplexedConnectionStream StreamConnection { get; }

    /// <summary>
    /// The request stream's wire ID (client-initiated bidirectional: 0, 4, 8, …), derived when the
    /// stream was accepted (see <see cref="Http3ConnectionContext"/>). Keys the QPACK decoder-stream
    /// instructions for the stream.
    /// </summary>
    public long StreamId { get; }

    /// <summary>
    /// The transport's request-body stream for this exchange — the innermost stream, independent of
    /// any wrapper a request interceptor installed on <see cref="HttpRequest.Body"/>. The send path
    /// consults it for a rejected body (413 over the size cap, 408 below the minimum data rate, 431 for
    /// an oversized trailer section), for a transport reset, and to stop reading the request stream
    /// once the complete response is on the wire.
    /// </summary>
    public Http3RequestBodyStream RequestBody { get; }

    /// <summary>
    /// The effective RFC 9218 priority derived from this request's <c>Priority</c>
    /// header (urgency 3, non-incremental by default). HTTP/3 delegates cross-stream
    /// response ordering to the QUIC transport, so this is observable engine state
    /// rather than an input to an explicit scheduler (see docs/DESIGN.md).
    /// </summary>
    public HttpPriority EffectivePriority { get; set; } = HttpPriority.Default;

    /// <summary>
    /// The extended CONNECT tunnel accepted on this exchange (RFC 9220), or <see langword="null"/>. Set
    /// by the accept path before the tunnel's response head is written.
    /// </summary>
    public Http3ExtendedConnectStream? Tunnel { get; set; }

    /// <summary>
    /// The connection that built this exchange, so ending the exchange tells it the request stream no
    /// longer keeps the connection busy (#1085); <see langword="null"/> until the exchange is built.
    /// </summary>
    public Http3ConnectionContext? Connection { get; set; }

    /// <summary>
    /// An accepted extended CONNECT tunnel takes the exchange's request stream over, so the exchange
    /// reports <see cref="HttpExchangeDirective.TakeOver"/> and the raw response body sink refuses to
    /// commit a second head; otherwise the base's abort/continue derivation applies.
    /// </summary>
    internal override HttpExchangeDirective ExchangeDirective =>
        Tunnel is not null ? HttpExchangeDirective.TakeOver : base.ExchangeDirective;

    /// <summary>
    /// Stops treating request-stream closure as cancellation after the complete response has flushed.
    /// The final FIN and local refusal of unread input can close the stream successfully.
    /// </summary>
    internal void StopObservingStreamClosure() => _streamClosedRegistration.Unregister();

    /// <summary>
    /// Claims the end of the exchange: its <c>SendAsync</c> returned or threw, or it was disposed. A
    /// <c>SendAsync</c> that refused the head or a buffered trailer section (#1183) does not claim it:
    /// nothing reached the wire and the response has not started, so the exchange stays running until
    /// the caller finalizes it again or disposes it.
    /// </summary>
    /// <returns><see langword="true"/> for the first caller only.</returns>
    public bool TryEndExchange()
    {
        return Interlocked.Exchange(ref _exchangeEnded, 1) == 0;
    }

    /// <summary>
    /// Takes the request stream's writer for an interim response, waiting for any earlier interim.
    /// Once the final head claims it, application writes are refused and automatic 100 is dropped.
    /// The successful caller releases it with <see cref="EndInterim"/> after the write ends.
    /// </summary>
    /// <returns><see langword="true"/> when the caller may write the interim response.</returns>
    public async ValueTask<bool> TryBeginInterimAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task preceding;
            lock (_responseHeadLock)
            {
                if (_finalHeadClaimed)
                {
                    return false;
                }

                if (_interimWritten is null)
                {
                    _interimWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    return true;
                }

                preceding = _interimWritten.Task;
            }

            await preceding.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the writer after an interim response, letting a claimed final head proceed.
    /// </summary>
    public void EndInterim()
    {
        TaskCompletionSource? written;
        lock (_responseHeadLock)
        {
            written = _interimWritten;
            _interimWritten = null;
        }

        written?.TrySetResult();
    }

    /// <summary>
    /// Takes the request stream's writer for the final response head, before its HEADERS frame is written —
    /// on the buffered send path, at the streamed sink's head commit and when accepting a tunnel.
    /// Further interims are refused (automatic <c>100 Continue</c> is dropped). An active interim is waited for, so the two frames
    /// never share the writer and the interim one precedes the final one on the wire (RFC 9114 §4.1).
    /// </summary>
    /// <returns>A task that completes once the final head may be written.</returns>
    public ValueTask ClaimFinalResponseHeadAsync()
    {
        lock (_responseHeadLock)
        {
            _finalHeadClaimed = true;
            return _interimWritten is { } written ? new ValueTask(written.Task) : ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Ends the exchange on its connection, then disposes it. A host that never finalizes the exchange
    /// through <c>SendAsync</c> still lets the connection fall idle by disposing it (#1085).
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            StopObservingStreamClosure();
            Connection?.EndExchange(this);
            await base.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // A host may dispose without sending, or after a failed send. Release that stream too,
            // after the exchange's cleanup. Do not await peer delivery here: the multiplexed
            // connection owns the delivery wait and its CloseDeliveryTimeout (#1424).
            _ = ReleaseRequestStreamAsync();
        }
    }

    private async Task ReleaseRequestStreamAsync()
    {
        try
        {
            await StreamConnection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ConnectionException
            or InvalidOperationException or OperationCanceledException)
        {
            // Teardown is best effort when the stream or its connection has already ended.
        }
    }

    private sealed class DebugView
    {
        private readonly Http3Context _target;

        public DebugView(Http3Context value)
        {
            _target = value;
        }

        public string? ExtendedConnectProtocol => _target.ExtendedConnectProtocol;
        public IMultiplexedConnectionStream StreamConnection => _target.StreamConnection;
        public long StreamId => _target.StreamId;
        public Http3RequestBodyStream RequestBody => _target.RequestBody;
        public HttpPriority EffectivePriority => _target.EffectivePriority;
        public Http3ExtendedConnectStream? Tunnel => _target.Tunnel;
        public Http3ConnectionContext? Connection => _target.Connection;
        public global::Assimalign.Cohesion.Http.HttpVersion Version => _target.Version;
        public global::Assimalign.Cohesion.Http.Connections.Internal.TransportHttpRequest Request => _target.Request;
        public global::Assimalign.Cohesion.Http.Connections.Internal.TransportHttpResponse Response => _target.Response;
        public global::Assimalign.Cohesion.Http.HttpConnectionInfo ConnectionInfo => _target.ConnectionInfo;
        public global::Assimalign.Cohesion.Http.HttpFeatureCollection Features => _target.Features;
        public global::System.Collections.Generic.IDictionary<string, object?> Items => _target.Items;
        public global::System.Threading.CancellationToken RequestCancelled => _target.RequestCancelled;
        public bool CancelRequested => _target.CancelRequested;
    }
}
