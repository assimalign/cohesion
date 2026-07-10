namespace Assimalign.Cohesion.Http;

/// <summary>
/// The result of evaluating a request's conditional preconditions against the target resource's
/// current validators (RFC 9110 &#167; 13.2.2).
/// </summary>
public enum HttpPreconditionOutcome
{
    /// <summary>
    /// All preconditions passed; the server should perform the requested method normally.
    /// </summary>
    Proceed = 0,

    /// <summary>
    /// A <c>GET</c> or <c>HEAD</c> request's <c>If-None-Match</c> / <c>If-Modified-Since</c>
    /// precondition indicates the client's cached representation is current; respond
    /// <c>304 Not Modified</c>.
    /// </summary>
    NotModified = 1,

    /// <summary>
    /// A precondition failed (an <c>If-Match</c> / <c>If-Unmodified-Since</c> guard, or a non-read
    /// method whose <c>If-None-Match</c> matched); respond <c>412 Precondition Failed</c>.
    /// </summary>
    PreconditionFailed = 2,
}
