using System;
using System.Net;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Internal;

public sealed class TcpServerTransportOptions
{
    private readonly TransportPipelineBuilder<TcpTransportConnection, TcpTransportConnectionContext> _builder;

    public TcpServerTransportOptions()
    {
        _builder = new TransportPipelineBuilder<TcpTransportConnection, TcpTransportConnectionContext>();
    }


    /// <summary>
    /// The endpoint in which the socket should listen on.
    /// </summary>
    public EndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8081);

    /// <summary>
    /// The number of I/O queues used to process requests. Set to 0 to directly schedule I/O to the ThreadPool.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="Environment.ProcessorCount" /> rounded down and clamped between 1 and 16.
    /// </remarks>
    public int IOQueueCount { get; set; } = Math.Min(Environment.ProcessorCount, 16);

    /// <summary>
    /// Wait until there is data available to allocate a buffer. Setting this to false can increase throughput at the cost of increased memory usage.
    /// </summary>
    /// <remarks>
    /// Defaults to true.
    /// </remarks>
    public bool WaitForDataBeforeAllocatingBuffer { get; set; } = true;

    /// <summary>
    /// Set to false to enable Nagle's algorithm for all connections.
    /// </summary>
    /// <remarks>
    /// Defaults to true.
    /// </remarks>
    public bool NoDelay { get; set; } = true;

    /// <summary>
    /// The maximum length of the pending connection middleware.
    /// </summary>
    /// <remarks>
    /// Defaults to 512.
    /// </remarks>
    public int Backlog { get; set; } = 512;

    /// <summary>
    /// Gets or sets the maximum unconsumed incoming bytes the transport will buffer.
    /// </summary>
    /// <remarks>
    /// Defaults to '1024 * 1024'.
    /// </remarks>
    public long? MaxReadBufferSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum outgoing bytes the transport will buffer before applying write back-pressure.
    /// </summary>
    /// <remarks>
    /// Defaults to '64 * 1024'.
    /// </remarks>
    public long? MaxWriteBufferSize { get; set; } = 64 * 1024;

    /// <summary>
    /// In-line application and transport continuations instead of dispatching to the thread-pool.
    /// </summary>
    /// <remarks>
    /// This will run application code on the IO thread which is why this is unsafe.
    /// It is recommended to set the DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS environment variable to '1' when using this setting to also in-line the completions
    /// at the runtime layer as well.
    /// This setting can make performance worse if there is expensive work that will end up holding onto the IO thread for longer than needed.
    /// Test to make sure this setting helps performance.
    /// </remarks>
    public bool UnsafePreferInLineScheduling { get; set; }

    /// <summary>
    /// Specifies whether or not when receiving data after a connection is initialized to 
    /// wait on either data receiving before 
    /// </summary>
    /// <remarks>
    /// Defaults to 'true'.
    /// </remarks>
    public bool WaitOnPacketIngestion { get; set; } = true;

    /// <summary>
    /// The default options.
    /// </summary>
    public static TcpServerTransportOptions Default { get; } = new TcpServerTransportOptions();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    public TcpServerTransportOptions Use(Func<TcpTransportConnection, TcpTransportConnectionContext, TransportMiddleware, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _builder.Use(middleware);

        return this;
    }

    internal TransportPipeline BuildPipeline()
    {
        return (TransportPipeline)(_builder as ITransportPipelineBuilder).Build();
    }

    internal SocketTransportConnectionSettings[] CreateConnectionSettings()
    {
        int count = IOQueueCount > 0 ? IOQueueCount : 1;

        return TransportPipeOptionsFactory.CreateSocketConnectionSettings(
            count,
            UnsafePreferInLineScheduling,
            WaitForDataBeforeAllocatingBuffer,
            MaxReadBufferSize,
            MaxWriteBufferSize);
    }
}
