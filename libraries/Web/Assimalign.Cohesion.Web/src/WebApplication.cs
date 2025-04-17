using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

public abstract class WebApplication : IWebApplication, IWebApplicationPipelineBuilder
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    IWebApplicationPipeline IWebApplicationPipelineBuilder.Build()
    {
        throw new NotImplementedException();
    }

    IWebApplicationPipelineBuilder IWebApplicationPipelineBuilder.Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        throw new NotImplementedException();
    }
}
