using System;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IDatagramConnection"/>.
/// </summary>
public abstract class DatagramConnection : IDatagramConnection
{
    private IConnectionFeatureCollection? _features;
    private object? _featuresSync;
    private ExceptionDispatchInfo? _featuresError;

    /// <inheritdoc />
    public abstract EndPoint LocalEndPoint { get; }

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
    public abstract ValueTask<DatagramReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask SendAsync(ReadOnlyMemory<byte> payload, EndPoint remoteEndPoint, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

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
