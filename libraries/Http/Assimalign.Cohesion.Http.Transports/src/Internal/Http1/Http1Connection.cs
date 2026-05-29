using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http1;

internal sealed class Http1Connection : HttpConnection
{
    private readonly ISingleStreamTransportConnection _connection;
    private Http1ConnectionContext? _openContext;

    public Http1Connection(
        ISingleStreamTransportConnection connection,
        bool isSecure,
        Func<IHttpFeatureCollection>? createFeatures)
        : base(connection, isSecure, createFeatures)
    {
        _connection = connection;
    }

    public override HttpConnectionContext Open()
    {
        return OpenAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public override async ValueTask<HttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_openContext is not null)
        {
            return _openContext;
        }

        ITransportConnectionContext transportContext = await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // The registration-time isSecure is a hint; the transport pipeline
        // may have established a secure session in the meantime (TLS
        // middleware wrapping the raw socket in SslStream, for example) and
        // recorded that via context.IsSecure. Promote either signal to the
        // effective value so HttpContext.ConnectionInfo.IsSecure reflects
        // the truth at request-read time.
        bool effectiveIsSecure = IsSecure || transportContext.IsSecure;
        _openContext = new Http1ConnectionContext(transportContext, effectiveIsSecure, CreateFeatures);

        return _openContext;
    }

    /// <summary>
    /// Performs an orderly HTTP/1.1 connection teardown.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item><description>Complete the transport pipe's output writer.
    ///   That signals the underlying transport's send loop that no further
    ///   bytes will be queued; the send loop drains its remaining backlog
    ///   (one final <c>Socket.SendAsync</c>, which waits for the kernel to
    ///   ACK the bytes) and then exits.</description></item>
    ///   <item><description>Wait briefly for the transport to transition
    ///   out of <see cref="ConnectionState.Open"/>. The send task closes
    ///   the socket in its own <c>finally</c> after the final
    ///   <c>Socket.SendAsync</c> completes; that state transition is our
    ///   signal that all queued response bytes are on the wire.</description></item>
    ///   <item><description>Dispose the underlying transport to release
    ///   socket / pool resources (idempotent after the transport's own
    ///   drain).</description></item>
    /// </list>
    /// <para>
    /// Mirrors the HTTP/2 fix (#686) for the same race: previously this
    /// method called <c>Connection.DisposeAsync()</c> directly, which
    /// aborts the socket immediately and can race the send task's
    /// in-flight <c>Socket.SendAsync</c>. The result was an HTTP/1.1
    /// client seeing "response ended prematurely" on the bytes the server
    /// had just committed via <c>WriteResponseAsync</c>. The graceful
    /// close + state-drain wait closes that gap so the response bytes are
    /// durably on the wire before the socket is torn down.
    /// </para>
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        if (_openContext is not null)
        {
            try
            {
                await CompleteOutputAsync(_openContext.Pipe).ConfigureAwait(false);
                await WaitForTransportDrainAsync(_connection).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort graceful close; fall through to hard disposal.
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask CompleteOutputAsync(ITransportConnectionPipe pipe)
    {
        try
        {
            // Completing the pipe's writer signals the transport's send
            // loop that no further bytes will be queued. The send loop
            // processes its remaining backlog (one final Socket.SendAsync,
            // which waits for the kernel to commit the bytes) and exits
            // cleanly. Idempotent — repeated completions are no-ops.
            await pipe.Output.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
            // The writer may already be completed if a teardown ran
            // concurrently. Swallowing here keeps the disposal idempotent.
        }
    }

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
        catch (OperationCanceledException)
        {
            // Best-effort: if the transport has not drained within the
            // timeout, fall through to disposal. The connection will be
            // aborted regardless.
        }
    }
}
