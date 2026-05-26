using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

/// <summary>
/// Defines options for creating a UDP server transport.
/// </summary>
public sealed class UdpServerTransportOptions
{
    private readonly TransportPipelineBuilder<UdpTransportConnectionContext> _builder;
    private readonly Lazy<TransportPipeline<UdpTransportConnectionContext>> _pipeline;

    /// <summary>
    /// Creates a new set of UDP server transport options.
    /// </summary>
    public UdpServerTransportOptions()
    {
        _builder = new TransportPipelineBuilder<UdpTransportConnectionContext>();
        _pipeline = new Lazy<TransportPipeline<UdpTransportConnectionContext>>(() => _builder.Build());
    }

    /// <summary>
    /// Gets or sets the endpoint that will be bound by the UDP server.
    /// </summary>
    public EndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8081);

    /// <summary>
    /// Gets or sets the preferred number of I/O queues.
    /// </summary>
    public int IOQueueCount { get; set; } = Math.Min(Environment.ProcessorCount, 16);

    /// <summary>
    /// Waits for available data before allocating buffers.
    /// </summary>
    public bool WaitForDataBeforeAllocatingBuffer { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum buffered read bytes.
    /// </summary>
    public long? MaxReadBufferSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum buffered write bytes.
    /// </summary>
    public long? MaxWriteBufferSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets whether scheduling may be inlined on I/O threads.
    /// </summary>
    public bool UnsafePreferInLineScheduling { get; set; }

    /// <summary>
    /// Gets or sets the number of newly discovered peers that can be queued for acceptance.
    /// </summary>
    /// <remarks>
    /// Use a value less than or equal to zero to allow an unbounded queue.
    /// </remarks>
    public int PendingAcceptQueueCapacity { get; set; } = 128;

    /// <summary>
    /// 
    /// </summary>
    internal TransportPipeline<UdpTransportConnectionContext> Pipeline => _pipeline.Value;

    /// <summary>
    /// Adds middleware to the UDP server transport pipeline.
    /// </summary>
    /// <param name="middleware">The middleware delegate to add.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="middleware"/> is <see langword="null"/>.</exception>
    public UdpServerTransportOptions Use(Func<UdpTransportConnectionContext, TransportMiddleware, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _builder.Use(middleware);
        return this;
    }


    internal TransportPipeOptionsContext CreatePipeOptions()
    {
        return TransportPipeOptionsFactory.CreatePipeOptions(MaxReadBufferSize, MaxWriteBufferSize, UnsafePreferInLineScheduling);
    }
}
