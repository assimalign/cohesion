namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// The location of a parameter within a request. See the <c>in</c> field of the "Parameter Object"
/// section of the OpenAPI Specification.
/// </summary>
public enum ParameterLocation
{
    /// <summary>A query string parameter.</summary>
    Query,

    /// <summary>A custom request header parameter.</summary>
    Header,

    /// <summary>A parameter that is part of the path template.</summary>
    Path,

    /// <summary>A parameter passed in a cookie.</summary>
    Cookie,

    /// <summary>
    /// A parameter describing the entire query string (OpenAPI 3.2+). A <c>querystring</c> parameter must
    /// use <c>content</c> rather than <c>schema</c>, and must not coexist with <c>query</c> parameters on
    /// the same operation.
    /// </summary>
    Querystring
}
