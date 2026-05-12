using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents an AMQP connection layered on top of a carrier transport connection.
/// </summary>
public abstract class AmqpConnection : IAmqpConnection
{
    private readonly ITransportConnection _connection;
    private readonly TransportId _transportId;

    /// <summary>
    /// Initializes a new AMQP connection.
    /// </summary>
    /// <param name="connection">The carrier transport connection.</param>
    /// <param name="transportId">The owning AMQP transport identifier.</param>
    protected AmqpConnection(ITransportConnection connection, TransportId transportId)
    {
        _connection = connection;
        _transportId = transportId;
    }

    /// <inheritdoc />
    public ConnectionId Id => _connection.Id;

    /// <inheritdoc />
    public TransportId TransportId => _transportId;

    /// <inheritdoc />
    public TransportProtocol Protocol => TransportProtocol.Amqp;

    /// <inheritdoc />
    public ConnectionState State => _connection.State;

    /// <inheritdoc />
    public void Abort()
    {
        _connection.Abort();
    }

    /// <inheritdoc />
    public ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        return _connection.AbortAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Opens the AMQP connection context.
    /// </summary>
    /// <returns>The opened AMQP connection context.</returns>
    public abstract AmqpConnectionContext Open();

    /// <summary>
    /// Opens the AMQP connection context.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the open operation.</param>
    /// <returns>The opened AMQP connection context.</returns>
    public abstract ValueTask<AmqpConnectionContext> OpenAsync(CancellationToken cancellationToken = default);

    IAmqpConnectionContext IAmqpConnection.Open()
    {
        return Open();
    }

    async ValueTask<IAmqpConnectionContext> IAmqpConnection.OpenAsync(CancellationToken cancellationToken)
    {
        return await OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
