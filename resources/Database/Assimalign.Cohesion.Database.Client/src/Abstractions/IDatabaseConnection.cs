using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// One authenticated client connection: a protocol session bound to a database on
/// the server, able to execute statements and stream their results back.
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

    /// <summary>
    /// Opens the connection: dials the transport and runs the
    /// startup/authenticate/ready handshake. A no-op when already open.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="DatabaseClientException">Thrown when the server rejects the handshake (version, authentication, unknown database, capacity).</exception>
    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes statement text on the server in the bound database's language and
    /// materializes the result.
    /// </summary>
    /// <param name="statement">The statement text.</param>
    /// <param name="parameters">Parameter values keyed by bare parameter name, or null when the statement takes none.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The materialized result: columns and rows for row-returning statements, the affected count otherwise.</returns>
    /// <exception cref="DatabaseClientException">Thrown when the server reports an error or the connection breaks mid-exchange. Statement-level failures (parse, execution) leave the connection usable.</exception>
    ValueTask<DatabaseClientResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default);
}
