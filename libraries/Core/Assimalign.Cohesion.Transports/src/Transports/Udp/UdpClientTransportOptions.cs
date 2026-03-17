using System;
using System.Net;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

/// <summary>
/// Defines options for creating a UDP client transport.
/// </summary>
public sealed class UdpClientTransportOptions
{
    private readonly TransportPipelineBuilder<UdpTransportConnection, UdpTransportConnectionContext> _builder;

    /// <summary>
    /// Creates a new set of UDP client transport options.
    /// </summary>
    public UdpClientTransportOptions()
    {
        _builder = new TransportPipelineBuilder<UdpTransportConnection, UdpTransportConnectionContext>();
    }

    /// <summary>
    /// Gets or sets the remote endpoint to connect to.
    /// </summary>
    public EndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8081);

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
    /// Adds middleware to the UDP client transport pipeline.
    /// </summary>
    /// <param name="middleware">The middleware delegate to add.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="middleware"/> is <see langword="null"/>.</exception>
    public UdpClientTransportOptions Use(Func<UdpTransportConnection, UdpTransportConnectionContext, TransportMiddleware, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _builder.Use(middleware);

        return this;
    }

    internal TransportPipeline BuildPipeline()
    {
        return (TransportPipeline)((ITransportPipelineBuilder)_builder).Build();
    }

    internal TransportPipeOptionsContext CreatePipeOptions()
    {
        return TransportPipeOptionsFactory.CreatePipeOptions(MaxReadBufferSize, MaxWriteBufferSize, UnsafePreferInLineScheduling);
    }
}
