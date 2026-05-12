namespace Assimalign.Cohesion.Transports;

/// <summary>
/// UDP transport trace codes.
/// </summary>
public enum UdpTraceCode
{
    /// <summary>
    /// No trace code.
    /// </summary>
    None = 0,

    /// <summary>
    /// Transport initialized.
    /// </summary>
    Initialized = 1,

    /// <summary>
    /// Connection opened.
    /// </summary>
    ConnectionOpened = 2,

    /// <summary>
    /// Connection closed.
    /// </summary>
    ConnectionClosed = 3,

    /// <summary>
    /// Connection error.
    /// </summary>
    ConnectionError = 4
}
