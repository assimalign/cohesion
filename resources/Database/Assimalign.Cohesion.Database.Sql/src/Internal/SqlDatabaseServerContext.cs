using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The SQL server's <see cref="IDatabaseServerContext"/>: the one engine the
/// server fronts and a live view of its active sessions.
/// </summary>
internal sealed class SqlDatabaseServerContext : IDatabaseServerContext
{
    private readonly SqlDatabaseServer _server;

    internal SqlDatabaseServerContext(SqlDatabaseServer server, IDatabaseEngine engine)
    {
        _server = server;
        Engine = engine;
    }

    /// <inheritdoc />
    public IDatabaseEngine Engine { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<IDatabaseServerSession> Sessions => _server.GetSessionsSnapshot();
}
