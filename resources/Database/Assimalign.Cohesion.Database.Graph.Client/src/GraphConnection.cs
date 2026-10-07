using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Graph.Client.Internal;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>One authenticated graph connection supporting one active exchange at a time.</summary>
/// <remarks>Scalar results are materialized. Paths stream until enumeration completes or is disposed.
/// An unfinished path exchange is discarded. Explicit graph transactions are not supported.</remarks>
public sealed class GraphConnection : IAsyncDisposable
{
    private readonly DatabaseConnection _connection;
    private int _disposed;

    internal GraphConnection(DatabaseConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Gets the database selected during the handshake.</summary>
    public string Database => _connection.Database;

    /// <summary>Gets whether the connection is open and available for use.</summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _connection.IsOpen;

    /// <summary>Executes a scalar projection or catalog statement and materializes its rows.</summary>
    /// <param name="statement">The GQL statement.</param>
    /// <param name="parameters">Named scalar parameters, when supported by the server language.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The columns, rows, and affected count.</returns>
    /// <exception cref="ArgumentException">The statement is empty.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed.</exception>
    /// <exception cref="GraphClientException">The server rejected the statement or the exchange failed.</exception>
    public async ValueTask<GraphResultSet> QueryAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var exchange = new GraphExecuteExchange(statement, parameters);
        try
        {
            return await _connection.ExecuteAsync(exchange, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseClientException exception)
        {
            throw new GraphClientException(exception.Code, exception.Message, exception);
        }
    }

    /// <summary>Executes a graph mutation and returns its affected entity count.</summary>
    /// <param name="statement">The GQL statement.</param>
    /// <param name="parameters">Named scalar parameters, when supported by the server language.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The affected count, or -1 for a row-returning statement.</returns>
    /// <exception cref="ArgumentException">The statement is empty.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed.</exception>
    /// <exception cref="GraphClientException">The server rejected the statement or the exchange failed.</exception>
    public async ValueTask<long> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        => (await QueryAsync(statement, parameters, cancellationToken).ConfigureAwait(false)).AffectedCount;

    /// <summary>Streams paths selected by a single bound path, node, or relationship variable.</summary>
    /// <param name="statement">A MATCH statement returning one bound path or entity variable.</param>
    /// <param name="parameters">Named scalar parameters, when supported by the server language.</param>
    /// <param name="cancellationToken">Cancellation token for the full enumeration.</param>
    /// <returns>Paths retaining node and relationship identities, labels, types, and properties.</returns>
    /// <exception cref="ArgumentException">The statement is empty.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed.</exception>
    /// <exception cref="GraphClientException">The query or stream failed; its connection is discarded.</exception>
    /// <remarks>Enumerate to completion to verify the server's terminal path count. Disposing an
    /// unfinished enumeration cancels the exchange and prevents reuse of that session.</remarks>
    public async IAsyncEnumerable<GraphPath> QueryPathsAsync(string statement,
        IReadOnlyDictionary<string, object?>? parameters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Stream stream;
        try
        {
            stream = await _connection.ExecuteStreamingAsync(new GraphPathsExchange(statement, parameters),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseClientException exception)
        {
            throw new GraphClientException(exception.Code, exception.Message, exception);
        }
        await using (stream.ConfigureAwait(false))
        {
            byte[] length = new byte[sizeof(int)];
            while (await ReadPathAsync(stream, length, cancellationToken).ConfigureAwait(false) is { } path)
            {
                yield return path;
            }
        }
    }

    /// <summary>Discards the rental and closes its session without returning it for reuse.</summary>
    /// <returns>The noncancellable teardown operation.</returns>
    public async ValueTask AbortAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _connection.AbortAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the connection to its pool, with its authenticated session intact when it is still
    /// healthy. Idempotent.
    /// </summary>
    /// <returns>A task that completes when the rental is returned.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask<GraphPath?> ReadPathAsync(Stream stream, byte[] length, CancellationToken cancellationToken)
    {
        try
        {
            int read = await stream.ReadAsync(length, cancellationToken).ConfigureAwait(false);
            if (read == 0) { return null; }
            await stream.ReadExactlyAsync(length.AsMemory(read), cancellationToken).ConfigureAwait(false);
            var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(length)];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            GraphProtocolPathMessage path = GraphProtocolPathMessage.Decode(payload);
            return new GraphPath(path.Nodes, path.Relationships);
        }
        catch (DatabaseClientException exception)
        {
            throw new GraphClientException(exception.Code, exception.Message, exception);
        }
    }
}
