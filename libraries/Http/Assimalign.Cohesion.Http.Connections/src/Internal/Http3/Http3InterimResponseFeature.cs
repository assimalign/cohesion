using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// HTTP/3 <see cref="IHttpInterimResponseFeature"/>. Emits an interim (<c>1xx</c>) response as an
/// additional QPACK-encoded HEADERS frame on the request stream, ahead of the final HEADERS frame
/// (RFC 9114 §4.1). The wire emission is delegated to
/// <see cref="Http3ConnectionContext.WriteInterimResponseAsync"/>.
/// </summary>
internal sealed class Http3InterimResponseFeature : IHttpInterimResponseFeature
{
    private readonly Http3ConnectionContext _connection;
    private readonly Http3Context _context;

    public Http3InterimResponseFeature(Http3ConnectionContext connection, Http3Context context)
    {
        _connection = connection;
        _context = context;
    }

    /// <inheritdoc />
    public string Name => "Assimalign.Cohesion.Http.InterimResponse";

    /// <inheritdoc />
    public bool IsInterimResponseSupported => !FinalResponseStarted;

    /// <summary>
    /// Whether the final response has already started — a streaming response feature committed the
    /// head — so an interim response can no longer precede it. The buffered response is written after
    /// the handler returns, so an in-handler interim always precedes it.
    /// </summary>
    private bool FinalResponseStarted => _context.ResponseBodySink?.HasStarted ?? false;

    /// <inheritdoc />
    public async ValueTask SendInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default)
    {
        HttpInterimResponseRules.ValidateInterimStatusCode(statusCode);

        if (FinalResponseStarted)
        {
            throw new InvalidOperationException(
                "An interim response cannot be sent after the final HTTP/3 response has started.");
        }

        await _connection.WriteInterimResponseAsync(_context, statusCode, headers, cancellationToken).ConfigureAwait(false);
    }
}
