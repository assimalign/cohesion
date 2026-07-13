namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// The category of a <see cref="SqlClientException"/>, giving SQL callers a stable
/// failure taxonomy without reasoning about raw wire codes.
/// </summary>
public enum SqlClientErrorKind
{
    /// <summary>An unexpected client-local or server-internal failure.</summary>
    Internal = 0,

    /// <summary>Dialing, the handshake, or the transport failed — the connection never became usable.</summary>
    ConnectionFailure,

    /// <summary>The server rejected the session's credentials.</summary>
    AuthenticationFailure,

    /// <summary>The session principal is not authorized for the operation.</summary>
    NotAuthorized,

    /// <summary>The statement could not be parsed; the connection stays usable.</summary>
    ParseFailure,

    /// <summary>The statement failed during planning or execution; the connection stays usable.</summary>
    ExecutionFailure,

    /// <summary>The transaction was aborted by conflict or deadlock resolution.</summary>
    TransactionAborted,

    /// <summary>A frame violated the wire protocol; the connection is broken.</summary>
    ProtocolViolation,

    /// <summary>The server is shutting down or over capacity.</summary>
    Unavailable,

    /// <summary>A typed row value could not be read as the requested type (client-local).</summary>
    InvalidCast,
}
