namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// The standard HTTP methods that may carry an <see cref="OpenApiOperation"/> on a Path Item Object.
/// </summary>
/// <remarks>
/// OpenAPI 3.2 also allows non-standard methods through the <c>additionalOperations</c> map; those are
/// modeled separately on <see cref="OpenApiPathItem.AdditionalOperations"/> keyed by method name.
/// </remarks>
public enum OperationType
{
    /// <summary>The HTTP GET method.</summary>
    Get,

    /// <summary>The HTTP PUT method.</summary>
    Put,

    /// <summary>The HTTP POST method.</summary>
    Post,

    /// <summary>The HTTP DELETE method.</summary>
    Delete,

    /// <summary>The HTTP OPTIONS method.</summary>
    Options,

    /// <summary>The HTTP HEAD method.</summary>
    Head,

    /// <summary>The HTTP PATCH method.</summary>
    Patch,

    /// <summary>The HTTP TRACE method.</summary>
    Trace,

    /// <summary>The HTTP QUERY method (OpenAPI 3.2+), as defined by the IETF safe-method-with-body draft or its RFC successor.</summary>
    Query
}
