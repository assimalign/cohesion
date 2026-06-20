using System;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Defines the HTTP protocol versions that can be hosted by the connection listener.
/// </summary>
[Flags]
public enum HttpProtocol
{
    /// <summary>
    /// No HTTP protocol has been configured.
    /// </summary>
    None = 0,

    /// <summary>
    /// HTTP/1.1.
    /// </summary>
    Http11 = 1 << 0,

    /// <summary>
    /// HTTP/2.
    /// </summary>
    Http20 = 1 << 1,

    /// <summary>
    /// HTTP/3.
    /// </summary>
    Http30 = 1 << 2,

    /// <summary>
    /// Alias for HTTP/1.1.
    /// </summary>
    Http1 = Http11,

    /// <summary>
    /// Alias for HTTP/2.
    /// </summary>
    Http2 = Http20,

    /// <summary>
    /// Alias for HTTP/3.
    /// </summary>
    Http3 = Http30,

    /// <summary>
    /// All supported HTTP protocols.
    /// </summary>
    All = Http11 | Http20 | Http30
}
