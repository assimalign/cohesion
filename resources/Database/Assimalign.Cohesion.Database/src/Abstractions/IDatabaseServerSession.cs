using System;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// One authenticated client session on the server: the binding between a network
/// connection, an authenticated principal, and an engine <see cref="IDatabaseSession"/>.
/// </summary>
public interface IDatabaseServerSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier of this server session.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// Gets the protocol version negotiated with the client.
    /// </summary>
    ProtocolVersion ProtocolVersion { get; }

    /// <summary>
    /// Gets the name of the authenticated principal, or null while authentication
    /// is still in progress.
    /// </summary>
    string? Principal { get; }

    /// <summary>
    /// Gets the engine session this server session executes against, or null
    /// before startup completes.
    /// </summary>
    IDatabaseSession? DatabaseSession { get; }
}
