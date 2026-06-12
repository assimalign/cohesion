namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Describes the transport-level security applied to a connection.
/// </summary>
public enum ConnectionSecurity
{
    /// <summary>
    /// No transport-level security; data is sent in the clear.
    /// </summary>
    None = 0,

    /// <summary>
    /// Transport Layer Security (TLS) is applied to the connection.
    /// </summary>
    Tls = 1
}
