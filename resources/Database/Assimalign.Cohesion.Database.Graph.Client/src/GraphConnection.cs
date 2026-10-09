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
    // The operation names the event source writes: the public members that run a query.
    private const string QueryOperation = "Query";
    private const string ExecuteOperation = "Execute";
    private const string QueryPathsOperation = "QueryPaths";

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
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The exchange is canceled, which marks the connection broken.</exception>
    /// <exception cref="GraphClientException">The server rejected the statement or the exchange failed.</exception>
    public ValueTask<GraphResultSet> QueryAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        => QueryCoreAsync(QueryOperation, statement, parameters, cancellationToken);

    /// <summary>Executes a graph mutation and returns its affected entity count.</summary>
    /// <param name="statement">The GQL statement.</param>
    /// <param name="parameters">Named scalar parameters, when supported by the server language.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The affected count, or -1 for a row-returning statement.</returns>
    /// <exception cref="ArgumentException">The statement is empty.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The exchange is canceled, which marks the connection broken.</exception>
    /// <exception cref="GraphClientException">The server rejected the statement or the exchange failed.</exception>
    public async ValueTask<long> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        => (await QueryCoreAsync(ExecuteOperation, statement, parameters, cancellationToken).ConfigureAwait(false)).AffectedCount;

    /// <summary>Streams paths selected by a single bound path, node, or relationship variable.</summary>
    /// <param name="statement">A MATCH statement returning one bound path or entity variable.</param>
    /// <param name="parameters">Named scalar parameters, when supported by the server language.</param>
    /// <param name="cancellationToken">Cancellation token for the full enumeration.</param>
    /// <returns>Paths retaining node and relationship identities, labels, types, and properties.</returns>
    /// <exception cref="ArgumentException">The statement is empty.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The exchange is canceled, which marks the connection broken.</exception>
    /// <exception cref="GraphClientException">The query or stream failed; its connection is discarded.</exception>
    /// <remarks>Enumerate to completion to verify the server's terminal path count. Disposing an
    /// unfinished enumeration cancels the exchange and prevents reuse of that session.</remarks>
    public async IAsyncEnumerable<GraphPath> QueryPathsAsync(string statement,
        IReadOnlyDictionary<string, object?>? parameters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var exchange = new GraphPathsExchange(statement, parameters);
        long startTimestamp = GraphClientEventSource.Log.GetTimestamp();
        GraphClientEventSource.Log.QueryStart(this, QueryPathsOperation);

        // The query's end is written on every path, from the finally: Success once the enumeration
        // reached the server's terminal count; Error, after QueryFailed, for a failed open or read;
        // Cancelled for a cancellation, or for a caller that stopped reading and disposed the
        // enumerator early. An iterator cannot catch around a yield, so the open and each read
        // capture their own failure.
        long paths = 0;
        bool completed = false;
        Exception? failure = null;
        try
        {
            Stream stream;
            try
            {
                try
                {
                    stream = await _connection.ExecuteStreamingAsync(exchange, cancellationToken).ConfigureAwait(false);
                }
                catch (DatabaseClientException exception)
                {
                    throw new GraphClientException(exception.Code, exception.Message, exception);
                }
            }
            catch (Exception exception) when (GraphClientEventSource.CaptureFailure(exception, out failure))
            {
                // Unreachable: the filter records the failure and declines it.
                throw;
            }

            await using (stream.ConfigureAwait(false))
            {
                byte[] length = new byte[sizeof(int)];
                while (true)
                {
                    GraphPath? path;
                    try
                    {
                        path = await ReadPathAsync(stream, length, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (GraphClientEventSource.CaptureFailure(exception, out failure))
                    {
                        // Unreachable: the filter records the failure and declines it.
                        throw;
                    }

                    if (path is null)
                    {
                        break;
                    }

                    paths++;
                    yield return path;
                }

                // The enumeration reached the server's terminal count: the query is complete.
                completed = true;
            }
        }
        finally
        {
            string status = completed
                ? GraphClientEventSource.StatusSuccess
                : failure is null or OperationCanceledException ? GraphClientEventSource.StatusCancelled : GraphClientEventSource.StatusError;
            GraphClientEventSource.Log.QueryEnded(
                this,
                QueryPathsOperation,
                status,
                ReferenceEquals(status, GraphClientEventSource.StatusError) ? failure : null,
                paths,
                startTimestamp);
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

    /// <summary>
    /// Runs a scalar exchange for <see cref="QueryAsync"/> and <see cref="ExecuteAsync"/>, which
    /// differ only in the operation name the event source writes.
    /// </summary>
    private async ValueTask<GraphResultSet> QueryCoreAsync(string operation, string statement,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var exchange = new GraphExecuteExchange(statement, parameters);
        long startTimestamp = GraphClientEventSource.Log.GetTimestamp();
        GraphClientEventSource.Log.QueryStart(this, operation);

        // The query's end is written on every path, from the finally: QueryFailed and then
        // QueryStop(Error) for a failure, QueryStop(Cancelled) for a cancellation.
        GraphResultSet? result = null;
        Exception? failure = null;
        try
        {
            try
            {
                result = await _connection.ExecuteAsync(exchange, cancellationToken).ConfigureAwait(false);
            }
            catch (DatabaseClientException exception)
            {
                throw new GraphClientException(exception.Code, exception.Message, exception);
            }

            return result;
        }
        catch (Exception exception) when (GraphClientEventSource.CaptureFailure(exception, out failure))
        {
            // Unreachable: the filter records the failure and declines it.
            throw;
        }
        finally
        {
            GraphClientEventSource.Log.QueryEnded(this, operation, failure, result?.Count ?? -1, startTimestamp);
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
