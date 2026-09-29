using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>Composes a graph client from database settings and a transport factory.</summary>
public sealed class GraphClientOptions
{
    /// <summary>Gets or sets the database, principal, endpoint, and pool settings.</summary>
    public DatabaseConnectionSettings? Settings { get; set; }

    /// <summary>Gets or sets the statically composed transport factory.</summary>
    public IConnectionFactory? ConnectionFactory { get; set; }
}

