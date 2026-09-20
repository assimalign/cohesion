using System;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>Creates pooled clients for the graph protocol family.</summary>
public static class GraphClient
{
    /// <summary>Creates a graph client that opens connections on demand.</summary>
    /// <param name="options">The database settings and transport factory.</param>
    /// <returns>A pooling graph client.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="ArgumentException">The settings or factory are absent or invalid.</exception>
    public static IGraphClient Create(GraphClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Settings is null || options.ConnectionFactory is null)
        {
            throw new ArgumentException("Connection settings and a connection factory are required.", nameof(options));
        }
        return new DefaultGraphClient(DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = options.Settings,
            ConnectionFactory = options.ConnectionFactory,
            Family = GraphProtocol.Family,
        }));
    }
}

