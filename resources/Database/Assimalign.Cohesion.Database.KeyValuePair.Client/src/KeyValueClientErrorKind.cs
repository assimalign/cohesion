namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// The category of a <see cref="KeyValueClientException"/>, giving key-value
/// callers a stable failure taxonomy without reasoning about raw wire codes.
/// </summary>
public enum KeyValueClientErrorKind
{
    /// <summary>An unexpected client-local or server-internal failure.</summary>
    Internal = 0,

    /// <summary>Dialing, the handshake, or the transport failed — the connection never became usable.</summary>
    ConnectionFailure,

    /// <summary>The server rejected the session's credentials.</summary>
    AuthenticationFailure,

    /// <summary>The session principal is not authorized for the operation.</summary>
    NotAuthorized,

    /// <summary>The command violated the key-value grammar; the connection stays usable.</summary>
    ParseFailure,

    /// <summary>The command failed during execution — including the retryable first-updater-wins write conflict; the connection stays usable.</summary>
    ExecutionFailure,

    /// <summary>The transaction was aborted by conflict or deadlock resolution.</summary>
    TransactionAborted,

    /// <summary>A frame violated the wire protocol; the connection is broken.</summary>
    ProtocolViolation,

    /// <summary>The server is shutting down or over capacity.</summary>
    Unavailable,

    /// <summary>The server returned a result whose shape the client could not decode (client-local).</summary>
    MalformedResult,
}
