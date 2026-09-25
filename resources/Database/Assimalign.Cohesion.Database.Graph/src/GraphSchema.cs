using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Creates session-bound graph metadata access without changing the graph database contract.</summary>
public static class GraphSchema
{
    /// <summary>Binds metadata operations to a graph database session.</summary>
    /// <param name="database">The database to address.</param>
    /// <param name="session">A session belonging to that database.</param>
    /// <returns>Session-bound graph metadata operations.</returns>
    /// <exception cref="DatabaseException">The database or session is not from this graph engine.</exception>
    public static IGraphSchema Open(IGraphDatabase database, IDatabaseSession session)
    {
        if (database is not GraphDatabaseInstance instance) { throw new DatabaseException("COHDBG005: Unsupported graph database."); }
        return new GraphSchemaSession(instance, instance.RequireSession(session));
    }
}
