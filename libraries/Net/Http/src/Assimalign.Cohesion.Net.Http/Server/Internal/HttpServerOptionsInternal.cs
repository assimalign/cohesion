
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Logging;
using Assimalign.Cohesion.Net.Cryptography;
using Assimalign.Cohesion.Net.Transports;

internal sealed class HttpServerOptionsInternal : HttpServerOptions
{
    /// <summary>
    /// 
    /// </summary>
    public IServiceProvider? ServiceProvider { get; init; }
    /// <summary>
    /// 
    /// </summary>
    public ITransport? Transport { get; init; }
    /// <summary>
    /// 
    /// </summary>
    public IHttpContextExecutor Executor { get; init; }

    /// <summary>
    /// 
    /// </summary>
    public ICertificateManager CertificateManager { get; init; }
    /// <summary>
    /// 
    /// </summary>
    public ILogger? Logger { get; init; }
}
