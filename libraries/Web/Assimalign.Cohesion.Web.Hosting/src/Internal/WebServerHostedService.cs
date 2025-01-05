using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Internal;

using Assimalign.Cohesion.Hosting;

internal class WebServerHostedService : WebServer, IHostService
{
    public WebServerHostedService(WebServerOptions options) 
        : base(options)
    {

    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
