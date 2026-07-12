using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.Server;

/// <summary>
/// Options controlling the database server front-end.
/// </summary>
public sealed class DatabaseServerOptions
{
    /// <summary>
    /// Gets the engines the server dispatches sessions to. The host registers its
    /// engines here before creating the server; nothing is discovered at runtime.
    /// </summary>
    public IList<IDatabaseEngine> Engines { get; } = new List<IDatabaseEngine>();

    /// <summary>
    /// Gets or sets the bound transport listener the server accepts connections
    /// from. The host composes the listener (TCP, named pipe, in-memory, …) and
    /// retains ownership — the server never disposes it.
    /// </summary>
    public IConnectionListener? Listener { get; set; }

    /// <summary>
    /// Gets or sets the authenticator consulted during the session handshake.
    /// When null the server uses <see cref="DatabaseAuthenticator.AllowAll"/> —
    /// the MVP development posture, which accepts every principal. Production
    /// deployments must supply a real implementation.
    /// </summary>
    public IDatabaseAuthenticator? Authenticator { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent sessions the server accepts.
    /// Connections beyond the limit are rejected with an unavailable error.
    /// </summary>
    public int MaxSessions { get; set; } = 100;

    /// <summary>
    /// Gets or sets the time an unauthenticated connection may hold a slot before
    /// it is dropped.
    /// </summary>
    public TimeSpan AuthenticationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the idle time after which a session is closed. Set to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable idle eviction.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets the drain budget honored by <see cref="IDatabaseServer.StopAsync"/>
    /// before remaining sessions are aborted.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
