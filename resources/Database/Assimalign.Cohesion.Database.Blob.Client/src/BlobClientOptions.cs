using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Blob.Client;

/// <summary>Composes a Blob client from connection settings and a transport factory.</summary>
public sealed class BlobClientOptions
{
    /// <summary>Gets or sets the database, principal, endpoint, and pool settings.</summary>
    public DatabaseConnectionSettings? Settings { get; set; }

    /// <summary>Gets or sets the transport factory used to dial the Blob endpoint.</summary>
    public IConnectionFactory? ConnectionFactory { get; set; }
}
