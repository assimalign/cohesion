using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A per-exchange capability, resolved from <see cref="IHttpContext.Features"/>, that lets an
/// application emit one or more <em>interim</em> (<c>1xx</c>) responses ahead of the final response
/// — most usefully <c>100 Continue</c> (RFC 9110 §10.1.1) and <c>103 Early Hints</c> (RFC 8297).
/// </summary>
/// <remarks>
/// <para>
/// The feature is made available by registering
/// <see cref="HttpInterimResponses.CreateInterceptor"/> on the transport's response-interceptor list
/// (<c>HttpConnectionListenerOptions.ResponseInterceptors</c>), the same opt-in way response
/// streaming and protocol upgrade plug in. Its interceptor wraps the transport's exchange control
/// (<see cref="HttpResponseInterceptorContext.Control"/>) in this typed
/// feature and installs it on every exchange, so a handler resolves it with
/// <c>context.Features.Get&lt;IHttpInterimResponseFeature&gt;()</c> or the
/// <see cref="HttpInterimResponseExtensions.InterimResponse"/> convenience. The transport owns the
/// per-version wire emission and never references this package.
/// </para>
/// <para>
/// An interim response carries no body: a <c>100 Continue</c> carries no fields, a
/// <c>103 Early Hints</c> typically carries only <see cref="HttpHeaderKey.Link"/> fields. HTTP/2 and
/// HTTP/3 peers may receive several interim responses, all before the final one (RFC 9113 §8.1 /
/// RFC 9114 §4.1). <c>101 Switching Protocols</c> is deliberately <b>not</b> an interim response here
/// — it is a connection transition owned by <c>Assimalign.Cohesion.Http.ProtocolUpgrade</c> — so
/// passing it is rejected.
/// </para>
/// <para>
/// Emission is only possible while the final response has not started. Once the transport commits the
/// final response head (a buffered send, or the first byte of a streamed body), the exchange can no
/// longer carry an interim response: <see cref="IsInterimResponseSupported"/> flips to
/// <see langword="false"/> and a further <see cref="SendInterimResponseAsync"/> is rejected. Check
/// <see cref="IsInterimResponseSupported"/> before emitting so a caller can discover an unsupported
/// exchange state without provoking an exception.
/// </para>
/// </remarks>
public interface IHttpInterimResponseFeature : IHttpFeature
{
    /// <summary>
    /// Gets whether an interim (<c>1xx</c>) response can still be emitted for this exchange — the
    /// transport supports interim responses <b>and</b> the final response has not started. Flips to
    /// <see langword="false"/> once the final response head is committed, or when the exchange can no
    /// longer carry an interim response (for example its connection was taken over by a protocol
    /// upgrade).
    /// </summary>
    bool IsInterimResponseSupported { get; }

    /// <summary>
    /// Emits a single interim (<c>1xx</c>) response ahead of the final response.
    /// </summary>
    /// <param name="statusCode">
    /// The interim status code. MUST be in the <c>1xx</c> range (100–199) and MUST NOT be
    /// <c>101 Switching Protocols</c> (that transition is owned by the protocol-upgrade package).
    /// </param>
    /// <param name="headers">
    /// The interim response fields, or <see langword="null"/> for none. A <c>100 Continue</c> carries
    /// no fields; a <c>103 Early Hints</c> typically carries only <see cref="HttpHeaderKey.Link"/>
    /// fields. The fields carry no message body, so no <c>Content-Length</c> is emitted onto the wire.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the interim response has been flushed to the transport.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// <paramref name="statusCode"/> is outside the <c>1xx</c> range, or is <c>101</c>.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The final response has already started, so an interim response can no longer precede it.
    /// </exception>
    ValueTask SendInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default);
}
