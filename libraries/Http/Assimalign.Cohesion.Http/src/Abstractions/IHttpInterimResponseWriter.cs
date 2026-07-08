using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A transport capability, surfaced through
/// <see cref="HttpResponseInterceptorContext.InterimResponseWriter"/>, that lets a feature package
/// emit one or more <em>interim</em> (<c>1xx</c>) responses ahead of the final response
/// (RFC 9110 §15.2) — most usefully <c>100 Continue</c> (§10.1.1) and <c>103 Early Hints</c>
/// (RFC 8297).
/// </summary>
/// <remarks>
/// <para>
/// This is the generic seam behind interim responses, mirroring how
/// <see cref="HttpResponseInterceptorContext.ResponseBody"/> exposes the framed response sink and
/// <see cref="HttpResponseInterceptorContext.ConnectionTakeover"/> exposes the raw-stream takeover:
/// the transport owns the per-version wire emission (an HTTP/1.1 status line, an HTTP/2 HEADERS
/// block, an HTTP/3 QPACK field section — each with no body and no <c>Content-Length</c>), while a
/// feature package (<c>Assimalign.Cohesion.Http.InterimResponses</c>) wraps this capability in a
/// typed <c>IHttpInterimResponseFeature</c> and installs it on the exchange. The transport never
/// references the feature package.
/// </para>
/// <para>
/// Unlike <see cref="HttpResponseInterceptorContext.ConnectionTakeover"/> — populated only on
/// HTTP/1.1, whose exchange owns its whole connection — an interim response is meaningful on all
/// three versions (RFC 9113 §8.1 / RFC 9114 §4.1), so the capability is offered on every exchange
/// that runs response interceptors.
/// </para>
/// </remarks>
public interface IHttpInterimResponseWriter
{
    /// <summary>
    /// Gets whether an interim (<c>1xx</c>) response can still be emitted for this exchange — the
    /// final response has not started. Flips to <see langword="false"/> once the final response head
    /// is committed (a streamed body started, or on HTTP/1.1 the connection was taken over).
    /// </summary>
    bool CanWriteInterimResponse { get; }

    /// <summary>
    /// Emits a single interim (<c>1xx</c>) response ahead of the final response.
    /// </summary>
    /// <param name="statusCode">
    /// The interim status code. MUST be in the <c>1xx</c> range (100–199) and MUST NOT be
    /// <c>101 Switching Protocols</c> (that transition is owned by the protocol-upgrade capability).
    /// </param>
    /// <param name="headers">
    /// The interim response fields, or <see langword="null"/> for none. A <c>100 Continue</c> carries
    /// no fields; a <c>103 Early Hints</c> typically carries only <see cref="HttpHeaderKey.Link"/>
    /// fields. The fields carry no message body, so no <c>Content-Length</c> is emitted.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the interim response has been flushed to the transport.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// <paramref name="statusCode"/> is outside the <c>1xx</c> range, or is <c>101</c>.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The final response has already started, so an interim response can no longer precede it.
    /// </exception>
    ValueTask WriteInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default);
}
