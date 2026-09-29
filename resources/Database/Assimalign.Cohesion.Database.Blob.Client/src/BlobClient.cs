using System;
using Assimalign.Cohesion.Database.Blob.Client.Internal;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Blob.Client;

/// <summary>Creates pooled clients for the Blob message family.</summary>
public static class BlobClient
{
    /// <summary>Creates a client without opening any connections.</summary>
    /// <param name="options">The connection settings and transport factory.</param>
    /// <returns>A client whose connections bind to the configured database during handshake.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="ArgumentException">The settings or factory are absent or invalid.</exception>
    public static IBlobClient Create(BlobClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Settings is null || options.ConnectionFactory is null)
        {
            throw new ArgumentException("Connection settings and a connection factory are required.", nameof(options));
        }

        return new DefaultBlobClient(DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = options.Settings,
            ConnectionFactory = options.ConnectionFactory,
            Family = BlobProtocol.Family,
        }));
    }
}
