using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents a mutable collection of HTTP cookies. Used on both the
/// request side (parsed from <c>Cookie</c> headers) and the response side
/// (mutated to schedule <c>Set-Cookie</c> emission).
/// </summary>
/// <remarks>
/// The collection is intentionally untyped on direction (read vs write) so
/// callers can treat request and response cookies uniformly when iterating.
/// The lifecycle &mdash; whether the collection reflects an immutable parse
/// snapshot or a queue of pending emissions &mdash; is owned by the
/// surrounding <see cref="IHttpRequestCookieFeature"/> /
/// <see cref="IHttpResponseCookieFeature"/>.
/// </remarks>
public interface IHttpCookieCollection : ICollection<HttpCookie>;