using System;
using System.Net;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

public sealed class UdpClientTransportOptions
{
	private TransportTraceHandler onTrace = (code, data, message) => { };
	private TransportMiddlewareHandler middleware = context => Task.CompletedTask;

	/// <summary>
	/// The endpoint in which the socket should listen on.
	/// </summary>
	public EndPoint Endpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8081);
	/// <summary>
	/// Wait until there is data available to allocate a buffer. Setting this to false 
	/// can increase throughput at the cost of increased memory usage.
	/// </summary>
	/// <remarks>
	/// Defaults to true.
	/// </remarks>
	public bool WaitForDataBeforeAllocatingBuffer { get; set; } = true;
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
	/// The Middleware Chain to be executed on initialization.
	/// </summary>
	public TransportMiddlewareHandler Middleware => this.middleware;

	/// <summary>
	/// The trace handler for the transport.
	/// </summary>
	public TransportTraceHandler OnTrace => this.onTrace;

	/// <summary>
	/// Sets a raw trace handler.
	/// </summary>
	/// <param name="onTrace"></param>
	/// <exception cref="ArgumentNullException"></exception>
	public void AddTraceHandler(TransportTraceHandler onTrace)
	{
		if (onTrace is null)
		{
			throw new ArgumentNullException(nameof(onTrace));
		}

		this.onTrace = onTrace;
	}

	/// <summary>
	/// Sets the trace handler for the transport.
	/// </summary>
	/// <typeparam name="TConnectionData">Any connection data set during middleware invocation.</typeparam>
	/// <param name="onTrace"></param>
	/// <exception cref="ArgumentNullException"></exception>
	public void AddTraceHandler<TConnectionData>(Action<UdpTraceCode, TConnectionData, string?> onTrace)
	{
		if (onTrace is null)
		{
			throw new ArgumentNullException(nameof(onTrace));
		}
		this.onTrace = (data, code, message) =>
		{
			if (data is TConnectionData connectionData && code is not null)
			{
				onTrace.Invoke((UdpTraceCode)code, connectionData, message);
			}
		};
	}

	/// <summary>
	/// Configures a Middleware chain.
	/// </summary>
	/// <param name="configure"></param>
	/// <exception cref="ArgumentNullException"></exception>
	public void AddMiddleware(Action<TransportMiddlewareBuilder<UdpClientTransportContext, UdpClientTransportMiddleware>> configure)
	{
		if (configure is null)
		{
			throw new ArgumentNullException(nameof(configure));
		}

		var builder = new TransportMiddlewareBuilder<UdpClientTransportContext, UdpClientTransportMiddleware>();

		configure.Invoke(builder);

		middleware = builder.Build();
	}
}
