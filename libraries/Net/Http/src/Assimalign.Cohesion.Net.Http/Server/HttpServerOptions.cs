
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Cryptography;
using Assimalign.Cohesion.Net.Transports;

public partial class HttpServerOptions
{
    private IList<ITransport> transports;
    private ICertificateManager certificateManager;

    public HttpServerOptions()
    {
        this.transports = new List<ITransport>();
    }

    /// <summary>
    /// A user-friendly name for the server. This is represented in the 
    /// </summary>
    public string ServerName { get; set; } = "Cohesion.Net HTTP Server";
    /// <summary>
    /// 
    /// </summary>
    public bool DisableHttp2 { get; set; }
    /// <summary>
    /// 
    /// </summary>
    public bool DisableHttp3 { get; set; }
    /// <summary>
    /// 
    /// </summary>
    public bool DisableHttps { get; set; } = true;

    /// <summary>
    /// 
    /// </summary>
    /// <remarks>
    /// Timeouts usually occur on packet ingestion
    /// </remarks>
    public TimeSpan ConnectionTimeout { get; set;  }

    internal ICertificateManager CertificateManager => this.certificateManager;
   
}
