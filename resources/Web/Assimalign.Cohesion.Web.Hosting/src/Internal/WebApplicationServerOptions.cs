using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

using Assimalign.Cohesion.Http.Transports;

internal sealed class WebApplicationServerOptions
{

    public IWebApplicationPipeline? Pipeline {get; set;}
    public IHttpConnectionListener? Listener { get; set; }
    public Func<Task>? OnDisposeAsync { get; set; }
}
