using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal sealed class Http2Connection : HttpConnection
{
    private readonly ISingleStreamTransportConnection _connection;
    private Http2ConnectionContext? _context;

    public Http2Connection(ISingleStreamTransportConnection connection, bool isSecure)
        : base(connection, isSecure)
    {
        _connection = connection;
    }

    public override HttpConnectionContext Open()
    {
        return OpenAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public override async ValueTask<HttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_context is not null)
        {
            return _context;
        }

        ITransportConnectionContext context = await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _context = new Http2ConnectionContext(context, IsSecure);

        return _context;
    }

    /// <summary>
    /// Performs an orderly HTTP/2 connection teardown (RFC 9113 §6.8):
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item><description>Run the HTTP/2 graceful close on the context
    ///   — emits a <c>GOAWAY</c> frame and completes the transport's
    ///   send-pipe writer so the transport's send task drains any
    ///   remaining buffered frames and performs its final
    ///   <c>Socket.SendAsync</c>.</description></item>
    ///   <item><description>Wait for the transport to transition out of
    ///   <see cref="ConnectionState.Open"/>. The transport's send task
    ///   calls <c>Abort()</c> in its own <c>finally</c> after the last
    ///   Socket.SendAsync completes; that state transition is our signal
    ///   that all queued bytes are on the wire.</description></item>
    ///   <item><description>Dispose the underlying transport to release
    ///   socket / pool resources (idempotent after the transport's own
    ///   Abort).</description></item>
    /// </list>
    /// <para>
    /// Fixes #686 — previously this method called
    /// <c>Connection.DisposeAsync()</c> directly, which aborts the socket
    /// immediately and can race the send task's in-flight
    /// <c>Socket.SendAsync</c>. The graceful close + state wait closes
    /// that gap so the response bytes a server's <c>SendAsync</c> just
    /// committed are durably on the wire before the socket is torn down.
    /// </para>
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            try
            {
                await _context.GracefulCloseAsync().ConfigureAwait(false);
                await WaitForTransportDrainAsync(_connection).ConfigureAwait(false);
            }
            finally
            {
                await _context.DisposeAsync().ConfigureAwait(false);
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Polls <see cref="ITransportConnection.State"/> for a transition
    /// out of <see cref="ConnectionState.Open"/>. Bounded to avoid
    /// hanging on a stuck transport.
    /// </summary>
    /// <remarks>
    /// For a real socket transport (e.g. <c>SocketTransportConnectionContext</c>),
    /// the send task closes the socket in its own <c>finally</c> after the
    /// final <c>Socket.SendAsync</c> completes — the state transition is our
    /// signal that the buffered bytes are on the wire. In localhost loopback
    /// scenarios this happens within milliseconds.
    ///
    /// For transports that don't operate a background send task (e.g. in-memory
    /// test pipes), the state never transitions until <c>DisposeAsync</c> is
    /// invoked. The timeout here is sized so those scenarios complete quickly:
    /// 200ms is comfortably above the localhost send latency and short enough
    /// that test-transport disposals don't accumulate.
    /// </remarks>
    private static async ValueTask WaitForTransportDrainAsync(ITransportConnection connection, int timeoutMs = 200)
    {
        using CancellationTokenSource cts = new(timeoutMs);

        try
        {
            while (connection.State == ConnectionState.Open)
            {
                await Task.Delay(1, cts.Token).ConfigureAwait(false);
            }
        }
        catch (System.OperationCanceledException)
        {
            // Best-effort: if the transport has not drained within the
            // timeout, fall through to disposal. The connection will be
            // aborted regardless.
        }
    }
}
