using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// One authenticated client connection: a protocol session bound to a database on
/// the server and one immutable model message family.
/// </summary>
/// <remarks>
/// Connections are not thread-safe — one exchange at a time, mirroring the
/// engine-session contract on the server side. Pooled connections return to their
/// pool on dispose; standalone semantics belong to the pool implementation.
/// </remarks>
public interface IDatabaseConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the database this connection is bound to.
    /// </summary>
    string Database { get; }

    /// <summary>
    /// Gets the principal this connection authenticated as.
    /// </summary>
    string Principal { get; }

    /// <summary>
    /// Gets the protocol version negotiated with the server, or default before
    /// the connection opens.
    /// </summary>
    ProtocolVersion ServerVersion { get; }

    /// <summary>
    /// Gets a value indicating whether the connection is open and usable.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>Discards this rental and closes its session instead of returning it to the pool.</summary>
    /// <returns>The asynchronous transport teardown operation.</returns>
    /// <remarks>Use when application-level session state cannot be reset safely. Aborting cancels
    /// and joins an active exchange. It cannot undo a transaction already committed by the server
    /// or determine an unacknowledged command's outcome. This operation is deliberately not cancellable:
    /// an unsafe rental must not survive cleanup merely because its caller cancelled.</remarks>
    ValueTask AbortAsync();

    /// <summary>Gets the message family fixed when the owning pool was created.</summary>
    ProtocolMessageFamily Family { get; }

    /// <summary>
    /// Opens the connection: dials the transport and runs the
    /// startup/authenticate/ready handshake. A no-op when already open.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="DatabaseClientException">Thrown when the server rejects the handshake (version, authentication, unknown database, capacity).</exception>
    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes one complete model-owned framed exchange.
    /// </summary>
    /// <typeparam name="TResult">The model's result type.</typeparam>
    /// <param name="exchange">The operation, bound to this connection's exact family instance.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The model-owned result.</returns>
    /// <exception cref="ArgumentNullException">The exchange is null.</exception>
    /// <exception cref="ArgumentException">The exchange belongs to a different family.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The rental has already been returned.</exception>
    /// <exception cref="DatabaseClientException">The server reports an error or the connection fails.</exception>
    ValueTask<TResult> ExecuteAsync<TResult>(IDatabaseProtocolExchange<TResult> exchange, CancellationToken cancellationToken = default);

    /// <summary>Starts a bounded download while its framed exchange remains active.</summary>
    /// <param name="exchange">The model operation, bound to this connection's exact family instance.</param>
    /// <param name="cancellationToken">Cancels startup and the entire returned stream's lifetime.</param>
    /// <returns>A sequential readable stream after the model validates its opening response.</returns>
    /// <exception cref="ArgumentNullException">The exchange is null.</exception>
    /// <exception cref="ArgumentException">The exchange belongs to another family.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="DatabaseClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The rental has already been returned.</exception>
    /// <remarks>
    /// Dispose the returned stream. Early disposal or cancellation aborts the exchange and returns its
    /// unusable rental. Verified completion retains this connection's lease for subsequent operations;
    /// dispose the connection to return it to the pool. Disposing the connection cancels and joins any
    /// active exchange. Later failures surface from stream reads and cannot appear as successful EOF.
    /// </remarks>
    ValueTask<Stream> ExecuteStreamingAsync(IDatabaseStreamingExchange exchange, CancellationToken cancellationToken = default);
}
