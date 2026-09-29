namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Stable error codes carried by <see cref="ProtocolMessageType.Error"/> frames.
/// </summary>
/// <remarks>
/// Codes are part of the wire contract: values are append-only and never renumbered.
/// </remarks>
public enum ProtocolErrorCode : ushort
{
    /// <summary>An unexpected internal server error.</summary>
    Internal = 0,

    /// <summary>The requested protocol version is not supported.</summary>
    UnsupportedVersion = 1,

    /// <summary>Authentication failed.</summary>
    AuthenticationFailed = 2,

    /// <summary>The session principal is not authorized for the operation.</summary>
    NotAuthorized = 3,

    /// <summary>The requested database does not exist.</summary>
    DatabaseNotFound = 4,

    /// <summary>The query or command could not be parsed; details carry diagnostics.</summary>
    ParseFailure = 5,

    /// <summary>The query or command failed during execution.</summary>
    ExecutionFailure = 6,

    /// <summary>The transaction was aborted by conflict or deadlock resolution.</summary>
    TransactionAborted = 7,

    /// <summary>A frame violated the protocol (bad framing, out-of-order message).</summary>
    ProtocolViolation = 8,

    /// <summary>The server is shutting down or over capacity.</summary>
    Unavailable = 9,
}
