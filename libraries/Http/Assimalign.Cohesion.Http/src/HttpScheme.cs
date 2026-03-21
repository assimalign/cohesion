namespace Assimalign.Cohesion.Http;

/// <summary>
/// Defines the URI scheme associated with an HTTP request.
/// </summary>
public enum HttpScheme
{
    /// <summary>
    /// The scheme is unknown.
    /// </summary>
    None = 0,

    /// <summary>
    /// The request uses the <c>http</c> scheme.
    /// </summary>
    Http = 1,

    /// <summary>
    /// The request uses the <c>https</c> scheme.
    /// </summary>
    Https = 2,
}
