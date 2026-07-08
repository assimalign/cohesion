using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// HTTP/2 <see cref="IHttpInterimResponseWriter"/> — the transport capability the interim-response
/// feature package taps through <see cref="HttpResponseInterceptorContext.InterimResponseWriter"/>.
/// Emits an interim (<c>1xx</c>) response as an additional HEADERS block on the exchange's stream,
/// ahead of the final response (RFC 9113 §8.1). The wire emission is delegated to
/// <see cref="Http2ConnectionContext.WriteInterimResponseAsync"/>, which holds the connection write
/// gate so the interim HEADERS never interleave with concurrent frames.
/// </summary>
internal sealed class Http2InterimResponseWriter : IHttpInterimResponseWriter
{
    private readonly Http2ConnectionContext _connection;
    private readonly Http2Context _context;

    public Http2InterimResponseWriter(Http2ConnectionContext connection, Http2Context context)
    {
        _connection = connection;
        _context = context;
    }

    /// <inheritdoc />
    public bool CanWriteInterimResponse => !FinalResponseStarted;

    /// <summary>
    /// Whether the final response has already started — a streaming response feature committed the
    /// head — so an interim response can no longer precede it. The buffered response is written after
    /// the handler returns, so an in-handler interim always precedes it.
    /// </summary>
    private bool FinalResponseStarted => _context.ResponseBodySink?.HasStarted ?? false;

    /// <inheritdoc />
    public async ValueTask WriteInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default)
    {
        HttpInterimResponseRules.ValidateInterimStatusCode(statusCode);

        if (FinalResponseStarted)
        {
            throw new InvalidOperationException(
                "An interim response cannot be sent after the final HTTP/2 response has started.");
        }

        await _connection.WriteInterimResponseAsync(_context, statusCode, headers, cancellationToken).ConfigureAwait(false);
    }
}
