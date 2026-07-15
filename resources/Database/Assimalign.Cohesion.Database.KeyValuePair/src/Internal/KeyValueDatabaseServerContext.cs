using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// The key-value server's <see cref="IDatabaseServerContext"/>: the one engine
/// the server fronts and a live view of its active sessions.
/// </summary>
internal sealed class KeyValueDatabaseServerContext : IDatabaseServerContext
{
    private readonly KeyValueDatabaseServer _server;

    internal KeyValueDatabaseServerContext(KeyValueDatabaseServer server, IDatabaseEngine engine)
    {
        _server = server;
        Engine = engine;
    }

    /// <inheritdoc />
    public IDatabaseEngine Engine { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<IDatabaseServerSession> Sessions => _server.GetSessionsSnapshot();
}
