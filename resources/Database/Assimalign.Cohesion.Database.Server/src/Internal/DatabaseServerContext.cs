using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Server.Internal;

/// <summary>
/// The default <see cref="IDatabaseServerContext"/>: the one engine the server
/// fronts and a live view of its active sessions.
/// </summary>
internal sealed class DatabaseServerContext : IDatabaseServerContext
{
    private readonly DatabaseServer _server;

    internal DatabaseServerContext(DatabaseServer server, IDatabaseEngine engine)
    {
        _server = server;
        Engine = engine;
    }

    /// <inheritdoc />
    public IDatabaseEngine Engine { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<IDatabaseServerSession> Sessions => _server.GetSessionsSnapshot();
}
