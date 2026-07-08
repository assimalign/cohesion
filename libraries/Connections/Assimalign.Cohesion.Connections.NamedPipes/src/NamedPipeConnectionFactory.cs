using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.NamedPipes.Internal;

namespace Assimalign.Cohesion.Connections.NamedPipes;

/// <summary>
/// Establishes outbound, reliable, ordered single-stream named-pipe connections to a
/// <see cref="NamedPipeEndPoint"/> (the client / dialing side of the named-pipe transport).
/// </summary>
/// <remarks>
/// Each call to <see cref="ConnectAsync(EndPoint, CancellationToken)"/> opens a fresh
/// <see cref="NamedPipeClientStream"/> and waits for the server pipe to become available, then returns a
/// live <see cref="Connection"/>.
/// </remarks>
public sealed class NamedPipeConnectionFactory : ConnectionFactory
{
    private readonly NamedPipeConnectionFactoryOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamedPipeConnectionFactory"/> class with default options.
    /// </summary>
    public NamedPipeConnectionFactory()
        : this(NamedPipeConnectionFactoryOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NamedPipeConnectionFactory"/> class.
    /// </summary>
    /// <param name="options">The pipe-tuning options for outbound connections.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public NamedPipeConnectionFactory(NamedPipeConnectionFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.NamedPipe,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        ConnectionSecurity.None);

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="endPoint"/> is not a <see cref="NamedPipeEndPoint"/>.</exception>
    public override async ValueTask<Connection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        if (endPoint is not NamedPipeEndPoint namedPipeEndPoint)
        {
            throw new NotSupportedException(
                $"The named-pipe factory can only connect to a {nameof(NamedPipeEndPoint)}.");
        }

        PipeOptions pipeOptions = PipeOptions.Asynchronous;

        if (_options.WriteThrough)
        {
            pipeOptions |= PipeOptions.WriteThrough;
        }

        NamedPipeClientStream client = new(
            namedPipeEndPoint.ServerName,
            namedPipeEndPoint.PipeName,
            PipeDirection.InOut,
            pipeOptions,
            _options.ImpersonationLevel,
            HandleInheritability.None);

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            return new NamedPipeConnection(client, localEndPoint: null, remoteEndPoint: namedPipeEndPoint);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a new <see cref="NamedPipeConnectionFactory"/> configured by the supplied delegate.
    /// </summary>
    /// <param name="configure">A delegate used to configure the factory options.</param>
    /// <returns>A new <see cref="NamedPipeConnectionFactory"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static NamedPipeConnectionFactory Create(Action<NamedPipeConnectionFactoryOptions> configure)
        => new(NamedPipeConnectionFactoryOptions.Create(configure));
}
