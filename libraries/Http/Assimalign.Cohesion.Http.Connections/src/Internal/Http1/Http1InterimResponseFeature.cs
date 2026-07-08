using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// HTTP/1.1 <see cref="IHttpInterimResponseFeature"/>. Writes an interim (<c>1xx</c>) status line and
/// its fields directly onto the connection stream ahead of the final response (RFC 9110 §15.2). An
/// HTTP/1.1 exchange owns its whole connection and the handler is the sole writer for its duration, so
/// no frame-interleaving discipline is needed — the interim bytes simply precede the final response
/// bytes on the wire.
/// </summary>
internal sealed class Http1InterimResponseFeature : IHttpInterimResponseFeature
{
    private readonly Http1Context _context;
    private readonly Stream _stream;

    public Http1InterimResponseFeature(Http1Context context, Stream stream)
    {
        _context = context;
        _stream = stream;
    }

    /// <inheritdoc />
    public string Name => "Assimalign.Cohesion.Http.InterimResponse";

    /// <inheritdoc />
    public bool IsInterimResponseSupported => !FinalResponseStarted;

    /// <summary>
    /// Whether the final response has already started, so an interim response can no longer precede
    /// it: the connection was taken over (protocol upgrade / CONNECT tunnel finalized out-of-band), or
    /// a streaming response feature has committed the head.
    /// </summary>
    private bool FinalResponseStarted =>
        _context.ResponseFinalized || (_context.ResponseBodySink?.HasStarted ?? false);

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
                "An interim response cannot be sent after the final HTTP/1.1 response has started.");
        }

        await Http1MessageWriter.WriteInterimResponseAsync(_stream, statusCode, headers, cancellationToken).ConfigureAwait(false);
    }
}
