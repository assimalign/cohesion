using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Documents.Client;

/// <summary>Composes a document client from database settings and a transport factory.</summary>
public sealed class DocumentClientOptions
{
    /// <summary>Gets or sets the database, principal, endpoint, and pool settings.</summary>
    public DatabaseConnectionSettings? Settings { get; set; }

    /// <summary>Gets or sets the statically composed transport factory.</summary>
    public IDuplexConnectionFactory? ConnectionFactory { get; set; }
}
