namespace Assimalign.Cohesion.Http;

/// <summary>
/// Defines the supported HTTP protocol versions.
/// </summary>
public enum HttpVersion
{
    /// <summary>
    /// The version is unknown.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// HTTP/1.1.
    /// </summary>
    Http11 = 1,

    /// <summary>
    /// HTTP/2.
    /// </summary>
    Http20 = 2,

    /// <summary>
    /// HTTP/3.
    /// </summary>
    Http30 = 3,
}
