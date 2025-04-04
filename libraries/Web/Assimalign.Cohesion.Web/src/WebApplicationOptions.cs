
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;

public class WebApplicationOptions
{
    /// <summary>
    /// 
    /// </summary>
    public string ServerName { get; set; } = "Cohesion Web Server";

    /// <summary>
    /// 
    /// </summary>
    public IWebApplicationPipeline Pipeline { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public HttpConnectionOptions HttpOptions { get; set; }
}
