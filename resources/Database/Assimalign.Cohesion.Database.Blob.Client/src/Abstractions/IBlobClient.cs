using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Blob.Client;

/// <summary>Owns a pool of authenticated connections to one Blob database.</summary>
/// <remarks>Dispose rented connections before disposing the client.</remarks>
public interface IBlobClient : IAsyncDisposable
{
    /// <summary>Gets the immutable connection settings.</summary>
    DatabaseConnectionSettings Settings { get; }

    /// <summary>Rents an authenticated connection, waiting for a free pool slot when necessary.</summary>
    /// <param name="cancellationToken">Cancellation token for pool acquisition and handshake.</param>
    /// <returns>A connection that returns its lease when disposed.</returns>
    /// <exception cref="BlobClientException">The connection or handshake failed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    ValueTask<IBlobConnection> ConnectAsync(CancellationToken cancellationToken = default);
}
