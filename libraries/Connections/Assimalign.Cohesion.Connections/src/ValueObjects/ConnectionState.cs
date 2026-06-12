namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Represents the lifecycle state of a connection.
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// The connection has been created but not yet started.
    /// </summary>
    Idle = 0,

    /// <summary>
    /// The connection is being established.
    /// </summary>
    Opening = 1,

    /// <summary>
    /// The connection is established and able to transfer data.
    /// </summary>
    Open = 2,

    /// <summary>
    /// The connection was aborted before closing gracefully.
    /// </summary>
    Aborted = 3,

    /// <summary>
    /// The connection is closing gracefully.
    /// </summary>
    Closing = 4,

    /// <summary>
    /// The connection is fully closed.
    /// </summary>
    Closed = 5
}
