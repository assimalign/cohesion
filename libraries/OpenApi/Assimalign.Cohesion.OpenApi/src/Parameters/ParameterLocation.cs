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
    Cookie
}
