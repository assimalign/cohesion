using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Blob.Internal;

/// <summary>
/// The Blob server's <see cref="IDatabaseServerContext"/>: the one engine the
/// server fronts and a live view of its active sessions.
/// </summary>
internal sealed class BlobDatabaseServerContext : IDatabaseServerContext
{
    private readonly BlobDatabaseServer _server;

    internal BlobDatabaseServerContext(BlobDatabaseServer server, IDatabaseEngine engine)
    {
        _server = server;
        Engine = engine;
    }

    /// <inheritdoc />
    public IDatabaseEngine Engine { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<IDatabaseServerSession> Sessions => _server.GetSessionsSnapshot();
}
