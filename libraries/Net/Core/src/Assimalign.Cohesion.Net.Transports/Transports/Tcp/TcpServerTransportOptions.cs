
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

public sealed class TcpServerTransportOptions
{
	private TransportTraceHandler onTrace = (code, data, message) => { };
	private TransportMiddlewareHandler middleware = context => Task.CompletedTask;


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
	public void AddTraceHandler<TConnectionData>(Action<TcpConnectionTraceCode, TConnectionData, string?> onTrace)
	{
		if (onTrace is null)
		{
			throw new ArgumentNullException(nameof(onTrace));
		}
		this.onTrace = (data, code, message) =>
		{
			if (data is TConnectionData connectionData && code is not null)
			{
				onTrace.Invoke((TcpConnectionTraceCode)code, connectionData, message);
			}
		};
	}

	/// <summary>
	/// Configures a Middleware chain.
	/// </summary>
	/// <param name="configure"></param>
	/// <exception cref="ArgumentNullException"></exception>
	public void AddMiddleware(Action<TransportMiddlewareBuilder<TcpServerTransportContext, TcpServerTransportMiddleware>> configure)
	{
		if (configure is null)
		{
			throw new ArgumentNullException(nameof(configure));
		}

		var builder = new TransportMiddlewareBuilder<TcpServerTransportContext, TcpServerTransportMiddleware>();

		configure.Invoke(builder);

		middleware = builder.Build();
	}
}
