using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

using Transports;

public abstract class HttpTransportOptions
{
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
