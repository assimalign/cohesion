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

    /// <summary>Server → client: error; carries a <see cref="ProtocolErrorCode"/> and message.</summary>
    Error = 10,

    /// <summary>Either direction: liveness probe.</summary>
    Ping = 11,

    /// <summary>Either direction: liveness response.</summary>
    Pong = 12,

    /// <summary>Client → server: orderly session termination.</summary>
    Terminate = 13,
}
