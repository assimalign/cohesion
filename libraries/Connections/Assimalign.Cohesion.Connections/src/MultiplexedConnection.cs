using System;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IMultiplexedConnection"/>.
/// </summary>
/// <remarks>
/// Override <see cref="AcceptStreamAsync(CancellationToken)"/> and
/// <see cref="OpenStreamAsync(ConnectionDirection, CancellationToken)"/> to return the concrete
/// <see cref="Connection"/> stream type; the explicit interface implementations forward to them.
/// </remarks>
public abstract class MultiplexedConnection : IMultiplexedConnection
{
    private IConnectionFeatureCollection? _features;
    private object? _featuresSync;
    private ExceptionDispatchInfo? _featuresError;

    /// <inheritdoc />
    public abstract ConnectionId Id { get; }

    /// <inheritdoc />
    public abstract EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public abstract EndPoint? RemoteEndPoint { get; }

    /// <inheritdoc />
    public abstract ConnectionCapabilities Capabilities { get; }

    /// <inheritdoc />
    public IConnectionFeatureCollection Features => Volatile.Read(ref _features) ?? InitializeFeatures();

    /// <summary>
    /// Gets the wrapped connection's features, or <see langword="null"/> for an unlayered connection.
    /// </summary>
    protected virtual IConnectionFeatureCollection? InnerFeatures => null;

    /// <summary>
    /// Registers this connection's features once, on first access, before the collection is published.
    /// </summary>
    /// <param name="features">The collection that will be exposed by <see cref="Features"/>.</param>
    /// <remarks>Configure the supplied collection rather than accessing <see cref="Features"/> recursively.</remarks>
    protected virtual void ConfigureFeatures(IConnectionFeatureCollection features)
    {
    }

    /// <inheritdoc />
    public abstract ConnectionState State { get; }

    /// <inheritdoc />
    public abstract CancellationToken ConnectionClosed { get; }

    /// <inheritdoc />
    public abstract void Abort(Exception? reason = null);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Accepts the next inbound stream opened by the remote peer.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>The accepted stream as a <see cref="Connection"/>.</returns>
    public abstract ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new outbound stream to the remote peer.
    /// </summary>
    /// <param name="direction">
    /// The stream direction: <see cref="ConnectionDirection.Bidirectional"/> or
    /// <see cref="ConnectionDirection.WriteOnly"/> for an outbound unidirectional stream.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the open operation.</param>
    /// <returns>The opened stream as a <see cref="Connection"/>.</returns>
    public abstract ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default);

    async ValueTask<IConnection> IMultiplexedConnection.AcceptStreamAsync(CancellationToken cancellationToken)
        => await AcceptStreamAsync(cancellationToken).ConfigureAwait(false);

    async ValueTask<IConnection> IMultiplexedConnection.OpenStreamAsync(ConnectionDirection direction, CancellationToken cancellationToken)
        => await OpenStreamAsync(direction, cancellationToken).ConfigureAwait(false);

    private IConnectionFeatureCollection InitializeFeatures()
    {
        object? sync = Volatile.Read(ref _featuresSync);
        if (sync is null)
        {
            sync = Interlocked.CompareExchange(ref _featuresSync, new object(), null) ?? _featuresSync;
        }

        lock (sync!)
        {
            if (_features is not null)
            {
                return _features;
            }

            _featuresError?.Throw();
            try
            {
                var features = new ConnectionFeatureCollection(InnerFeatures);
                ConfigureFeatures(features);
                Interlocked.CompareExchange(ref _features, features, null);
                return features;
            }
            catch (Exception exception)
            {
                _featuresError = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }
    }
}
