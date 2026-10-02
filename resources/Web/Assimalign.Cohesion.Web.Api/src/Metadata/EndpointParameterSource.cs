namespace Assimalign.Cohesion.Web;

/// <summary>
/// Where a typed endpoint reads a request-bound parameter from, as described by
/// <see cref="EndpointParameterMetadata"/>.
/// </summary>
/// <remarks>
/// New sources are appended as binding grows (file uploads, #1061). A consumer that meets a value it does
/// not recognize should treat that parameter as undescribed rather than fail.
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
    Body
}
