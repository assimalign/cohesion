namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// The type discriminator carried in every protocol frame header.
/// </summary>
public enum ProtocolMessageType : byte
{
    /// <summary>Client → server: protocol version, requested database, client identity.</summary>
    Startup = 1,

    /// <summary>Server → client: authentication challenge or method selection.</summary>
    Authenticate = 2,

    /// <summary>Client → server: authentication response (credentials, token).</summary>
    AuthenticateResponse = 3,

    /// <summary>Server → client: session established; carries session parameters.</summary>
    Ready = 4,

    /// <summary>Client → server: execute a query or command in the session's language.</summary>
    Execute = 5,

    /// <summary>Server → client: result set header (column/shape metadata).</summary>
    ResultHeader = 6,

    /// <summary>Server → client: one result row/entry; repeated.</summary>
    ResultRow = 7,

    /// <summary>Server → client: result complete; carries affected count and status.</summary>
    ResultComplete = 8,

    /// <summary>Client → server: transaction control (begin, commit, rollback).</summary>
    Transaction = 9,

    /// <summary>Server → client: error; carries a <see cref="ProtocolErrorCode"/> and message.</summary>
    Error = 10,

    /// <summary>Either direction: liveness probe.</summary>
    Ping = 11,

    /// <summary>Either direction: liveness response.</summary>
    Pong = 12,

    /// <summary>Client → server: orderly session termination.</summary>
    Terminate = 13,
}
