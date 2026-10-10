namespace Assimalign.Cohesion.Web;

/// <summary>
/// Where a typed endpoint reads a request-bound parameter from, as described by
/// <see cref="EndpointParameterMetadata"/>.
/// </summary>
/// <remarks>
/// New sources are appended as binding grows, so existing values never change. A consumer that meets a
/// value it does not recognize should treat that parameter as undescribed rather than fail.
/// </remarks>
public enum EndpointParameterSource
{
    /// <summary>A route value the endpoint's template captures.</summary>
    Route,

    /// <summary>
    /// A route value when the matched route captured one, otherwise the query string. The source
    /// generator describes a parameter this way when it could not see the whole template: a route-group
    /// endpoint, whose prefix is declared elsewhere, or a pattern that is not a string literal. Resolve
    /// it against the built route's composed template: a parameter the template names is a route value,
    /// any other is a query-string value.
    /// </summary>
    RouteOrQuery,

    /// <summary>A query-string value.</summary>
    Query,

    /// <summary>A request header.</summary>
    Header,

    /// <summary>A form field of a form-encoded or multipart request body.</summary>
    Form,

    /// <summary>The request body, deserialized through the content-serialization registry.</summary>
    Body,

    /// <summary>
    /// Uploaded files of a <c>multipart/form-data</c> request body (RFC 7578). The parameter's
    /// <see cref="EndpointParameterMetadata.Type"/> says which: <c>IHttpFormFile</c> is the file sent under
    /// the field <see cref="EndpointParameterMetadata.Name"/>, a sequence of <c>IHttpFormFile</c> every file
    /// sent under that name, and <c>IHttpFormFileCollection</c> every uploaded file whatever its field name
    /// (its name is then the handler parameter's).
    /// </summary>
    FormFile
}
