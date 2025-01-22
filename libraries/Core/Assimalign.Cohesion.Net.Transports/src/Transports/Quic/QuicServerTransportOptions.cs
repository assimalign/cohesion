#if NET7_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

public sealed class QuicServerTransportOptions
{
    private TransportTraceHandler onTrace = (code, data, message) => { };
    private TransportMiddlewareHandler middleware = context => Task.CompletedTask;

    /// <summary>
    /// 
    /// </summary>
    public IPEndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8080);
    /// <summary>
	/// The maximum length of the pending connection middleware.
	/// </summary>
	/// <remarks>
	/// Defaults to 512.
	/// </remarks>
	public int Backlog { get; set; } = 512;
    /// <summary>
	/// The Middleware Chain to be executed on initialization.
	/// </summary>
	public TransportMiddlewareHandler Middleware => this.middleware;
    /// <summary>
    /// The trace handler for the transport.
    /// </summary>
    public TransportTraceHandler OnTrace => this.onTrace;


    /// <summary>
    /// Configures a Middleware chain.
    /// </summary>
    /// <param name="configure"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void AddMiddleware(Action<TransportMiddlewareBuilder<QuicServerTransportContext, QuicServerTransportMiddleware>> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var builder = new TransportMiddlewareBuilder<QuicServerTransportContext, QuicServerTransportMiddleware>();

        configure.Invoke(builder);

        middleware = builder.Build();
    }
}
#endif