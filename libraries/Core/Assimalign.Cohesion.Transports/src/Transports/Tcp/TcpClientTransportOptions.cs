using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

public sealed class TcpClientTransportOptions
{
	private TransportTrace onTrace = (code, data, message) => { };
    private readonly TransportPipelineBuilder<TcpTransportConnection, TcpTransportConnectionContext> _builder;

    public TcpClientTransportOptions()
    {
        _builder = new TransportPipelineBuilder<TcpTransportConnection, TcpTransportConnectionContext>();
		EventListeners = new List<TransportEventListener>();
    }

    /// <summary>
    /// The endpoint in which the socket should listen on.
    /// </summary>
    public EndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8081);

	/// <summary>
	/// Wait until there is data available to allocate a buffer. Setting this to false 
	/// can increase throughput at the cost of increased memory usage.
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
    /// The trace handler for the transport.
    /// </summary>
    public TransportTrace Trace { get; set; } = (a, b, c) => { };

    /// <summary>
    /// Returns a  collection of event listeners.
    /// </summary>
    public List<TransportEventListener> EventListeners { get; }

    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    public TcpClientTransportOptions Use(Func<TcpTransportConnection, TcpTransportConnectionContext, TransportMiddleware, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _builder.Use(middleware);

        return this;
    }

    internal TransportPipeline BuildPipeline()
    {
        return (TransportPipeline)(_builder as ITransportPipelineBuilder).Build();
    }
}
