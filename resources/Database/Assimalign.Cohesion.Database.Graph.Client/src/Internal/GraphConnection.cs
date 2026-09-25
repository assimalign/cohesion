using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client.Internal;

internal sealed class GraphConnection : IGraphConnection
{
    private readonly IDatabaseConnection _connection;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphConnection"/> class.
    /// </summary>
    /// <param name="connection">The rented database connection that carries graph exchanges.</param>
    public GraphConnection(IDatabaseConnection connection)
    {
        _connection = connection;
    }

    public string Database => _connection.Database;
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _connection.IsOpen;

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

    public async ValueTask<long> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        => (await QueryAsync(statement, parameters, cancellationToken).ConfigureAwait(false)).AffectedCount;

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

    public async ValueTask AbortAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _connection.AbortAsync().ConfigureAwait(false);
        }
    }

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
