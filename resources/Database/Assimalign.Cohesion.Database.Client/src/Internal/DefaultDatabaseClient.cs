using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// The default pooling client: a slot semaphore bounds total connections at the
/// settings' pool size, and an idle stack reuses authenticated sessions —
/// returning a healthy connection keeps its server session alive for the next
/// rent.
/// </summary>
internal sealed class DefaultDatabaseClient : IDatabaseClient
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ConcurrentStack<PooledDatabaseConnection> _idle = new();
    private readonly SemaphoreSlim _slots;
    private bool _isDisposed;

    internal DefaultDatabaseClient(DatabaseConnectionSettings settings, IConnectionFactory connectionFactory)
    {
        Settings = settings;
        _connectionFactory = connectionFactory;
        _slots = new SemaphoreSlim(settings.MaxPoolSize, settings.MaxPoolSize);
    }

    /// <inheritdoc />
    public DatabaseConnectionSettings Settings { get; }

    /// <inheritdoc />
    public async ValueTask<IDatabaseConnection> RentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Prefer an idle, still-healthy connection — this is the session
            // reuse the pool exists for.
            while (_idle.TryPop(out PooledDatabaseConnection? idle))
            {
                if (idle.IsOpen)
                {
                    idle.MarkRented();
                    return idle;
                }

                await idle.CloseAsync().ConfigureAwait(false);
            }

            var connection = new PooledDatabaseConnection(this, _connectionFactory, Settings);

            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.CloseAsync().ConfigureAwait(false);
                throw;
            }

            connection.MarkRented();
            return connection;
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    /// <summary>
    /// Accepts a connection back from a rent: healthy connections go to the idle
    /// stack with their session intact; broken ones close. Always frees the slot.
    /// </summary>
    internal async ValueTask ReturnAsync(PooledDatabaseConnection connection)
    {
        if (!_isDisposed && connection.IsOpen)
        {
            _idle.Push(connection);
        }
        else
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }

        try
        {
            _slots.Release();
        }
        catch (ObjectDisposedException)
        {
            // The client was disposed while this connection was rented; nothing
            // waits on the slot anymore.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        while (_idle.TryPop(out PooledDatabaseConnection? idle))
        {
            await idle.CloseAsync().ConfigureAwait(false);
        }

        _slots.Dispose();
    }
}
