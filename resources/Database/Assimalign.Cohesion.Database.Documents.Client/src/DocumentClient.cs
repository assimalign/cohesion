using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client;

/// <summary>A pooled client for OQL, document mutations and collection operations.</summary>
/// <remarks>
/// It layers the document surface over the shared <see cref="DatabaseClient"/> core, which owns pooling,
/// framing and the wire handshake. Disposing the client closes every pooled connection.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("DocumentClient")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Client.DocumentClient.DebugView))]
public sealed class DocumentClient : IAsyncDisposable
{
    private readonly DatabaseClient _client;

    private DocumentClient(DatabaseClient client)
    {
        _client = client;
    }

    /// <summary>Creates a document client that opens connections on demand.</summary>
    /// <param name="options">The database settings and transport factory.</param>
    /// <returns>A pooling document client.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="ArgumentException">The settings or factory are absent or invalid.</exception>
    public static DocumentClient Create(DocumentClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Settings is null || options.ConnectionFactory is null)
        {
            throw new ArgumentException(ErrorMessages.ClientOptionsMissing, nameof(options));
        }
        return new DocumentClient(DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = options.Settings,
            ConnectionFactory = options.ConnectionFactory,
            Family = DocumentProtocol.Family,
        }));
    }

    /// <summary>Gets the endpoint, authentication, database, and pool settings.</summary>
    public DatabaseConnectionSettings Settings => _client.Settings;

    /// <summary>Rents an authenticated connection bound to the configured database.</summary>
    /// <param name="cancellationToken">Cancellation token for connecting.</param>
    /// <returns>The rented connection; dispose it to return its session.</returns>
    /// <exception cref="ObjectDisposedException">The client, or an object its connection factory needs, is disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> cancels the wait for a slot, the dial or the handshake.</exception>
    /// <exception cref="DocumentClientException">The dial failed (<see cref="ProtocolErrorCode.ConnectionFailure"/>; the inner <see cref="DatabaseClientException"/> keeps the transport's exception) or the handshake failed: the server's code, or <see cref="ProtocolErrorCode.Internal"/> when the transport broke during it (the inner <see cref="DatabaseClientException"/> keeps the transport's exception).</exception>
    public async ValueTask<DocumentConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return new DocumentConnection(await _client.RentAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (DatabaseClientException exception)
        {
            throw new DocumentClientException(exception.Code, exception.Message, exception);
        }
    }

    /// <summary>
    /// Closes every idle pooled connection; outstanding connections close when they are returned.
    /// </summary>
    /// <returns>A task that completes when the idle connections are closed.</returns>
    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Documents.Client.DocumentClient value)
        {
            _ = value;
        }
    }
}
