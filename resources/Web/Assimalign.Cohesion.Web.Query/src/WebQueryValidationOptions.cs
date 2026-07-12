using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Query;

using Assimalign.Cohesion.Http;

/// <summary>
/// Options for the QUERY request-validation middleware (see <c>UseQueryValidation</c>):
/// the resource's advertised <c>Accept-Query</c> set, the representations it can produce,
/// and the status-code policy for a missing or malformed <c>Content-Type</c>.
/// </summary>
/// <remarks>
/// The middleware snapshots these options when <c>UseQueryValidation</c> runs — mutations after
/// registration have no effect, matching the builder-time composition model of the Web pipeline.
/// </remarks>
public sealed class WebQueryValidationOptions
{
    private HttpStatusCode _invalidContentTypeStatusCode = HttpStatusCode.BadRequest;

    /// <summary>
    /// Gets the query-format media ranges the resource accepts as QUERY request content — the
    /// resource's <c>Accept-Query</c> set (RFC 10008 &#167; 3). When non-empty, a QUERY whose
    /// <c>Content-Type</c> no advertised range includes is rejected with
    /// <c>415 Unsupported Media Type</c>. When empty (the default), any syntactically valid
    /// <c>Content-Type</c> is allowed through and no acceptance check is performed.
    /// </summary>
    public IList<HttpMediaType> AcceptedMediaTypes { get; } = new List<HttpMediaType>();

    /// <summary>
    /// Gets the media types the resource can produce as QUERY responses, in server preference
    /// order. When non-empty, the request's <c>Accept</c> field is negotiated against this set
    /// (RFC 9110 &#167; 12.5.1) and an unsatisfiable <c>Accept</c> is rejected with
    /// <c>406 Not Acceptable</c>. When empty (the default), no response negotiation is performed.
    /// </summary>
    public IList<HttpMediaType> SupportedResponseMediaTypes { get; } = new List<HttpMediaType>();

    /// <summary>
    /// Gets or sets the status code used to reject a QUERY whose content carries a missing or
    /// malformed <c>Content-Type</c> (RFC 10008 &#167; 2.1 / &#167; 2.3). Either
    /// <see cref="HttpStatusCode.BadRequest"/> (the default — the request itself is defective) or
    /// <see cref="HttpStatusCode.UnsupportedMediaType"/> (treat an undeclared type as an
    /// unsupported one).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when set to a status other than <c>400</c> or <c>415</c>.
    /// </exception>
    public HttpStatusCode InvalidContentTypeStatusCode
    {
        get => _invalidContentTypeStatusCode;
        set
        {
            if (value.Value is not (400 or 415))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value.Value,
                    "A missing or malformed QUERY Content-Type is rejected with 400 Bad Request or 415 Unsupported Media Type.");
            }
            _invalidContentTypeStatusCode = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the middleware advertises the
    /// <see cref="AcceptedMediaTypes"/> set on responses via the <c>Accept-Query</c> response
    /// field (RFC 10008 &#167; 3), signaling that the resource supports the QUERY method.
    /// Defaults to <see langword="true"/>; a no-op while <see cref="AcceptedMediaTypes"/> is
    /// empty. The field is written before the rest of the pipeline runs, so an application
    /// handler can still override it.
    /// </summary>
    public bool AdvertiseAcceptQuery { get; set; } = true;
}
