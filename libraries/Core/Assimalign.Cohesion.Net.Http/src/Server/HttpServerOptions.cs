using System;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Transports;

public abstract class HttpServerOptions
{
    /// <summary>
    /// A user-friendly name for the server. This is represented in the 
    /// </summary>
    public string ServerName { get; set; } = "Cohesion .Net HTTP Server";
    /// <summary>
    /// 
    /// </summary>
    /// <remarks>
    /// Timeouts usually occur on packet ingestion
    /// </remarks>
    public TimeSpan ConnectionTimeout { get; set;  }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="version"></param>
    public abstract void UseHttp(HttpVersion version);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="version"></param>
    public abstract void UseHttps(HttpVersion version);
    /// <summary>
    /// Overrides the underlying HTTP Transports used for processing the connection.
    /// </summary>
    /// <param name="transport"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public abstract void UseTransport(ITransport transport);
    /// <summary>
    /// Overrides the underlying HTTP Transports used for processing the connection.
    /// </summary>
    /// <param name="configure"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public abstract void UseTransport(Func<ITransport> configure);
    /// <summary>
    /// Configures and adds the underlying TCP Transports that is used for HTTP/1.1 and HTTP/2.0
    /// </summary>
    /// <param name="configure"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public abstract void UseTcpTransport(Action<TcpServerTransportOptions> configure);
}