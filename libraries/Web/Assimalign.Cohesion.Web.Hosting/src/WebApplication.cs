using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Transports;

public sealed class WebApplication : Host<WebApplicationContext>, IWebApplication, IWebApplicationPipelineBuilder
{
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware;
    private readonly WebApplicationContext _context;
    private readonly WebApplicationOptions _options;

    private bool _isBuilt;

    internal WebApplication(WebApplicationContext context, WebApplicationOptions options) : base(options)
    {
        _context = context;
        _options = options;
        _middleware = new List<Func<WebApplicationMiddleware, WebApplicationMiddleware>>();
    }

    public override WebApplicationContext Context => _context;
    
    IWebApplicationPipeline IWebApplicationPipelineBuilder.Build()
    {
        if (_isBuilt)
        {
            ThrowHelper.ThrowInvalidOperationException("The web host is already built.");
        }
        throw new NotImplementedException();
    }
    IWebApplicationPipelineBuilder IWebApplicationPipelineBuilder.Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        ThrowHelper.ThrowIfNull(middleware);

        _middleware.Add(middleware);

        return this;
    }
    Task IWebApplication.StartAsync(CancellationToken cancellationToken)
    {
        return (this as IHost).StartAsync(cancellationToken);
    }
    Task IWebApplication.StopAsync(CancellationToken cancellationToken)
    {
        return (this as IHost).StopAsync(cancellationToken);
    }

    public static WebApplicationBuilder CreateBuilder()
    {
        return CreateBuilder(new WebApplicationOptions()
        {

        });
    }
    public static WebApplicationBuilder CreateBuilder(WebApplicationOptions options)
    {
        ThrowHelper.ThrowIfNull(options);

        return new WebApplicationBuilder(options);
    }
}
