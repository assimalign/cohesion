using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Web.Hosting.Internal;

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


    // TODO: Need to create an API that allows feature registration on HttpContext creation from the transport layer
    private void Init()
    {
        Use((context, next) =>
        {
            var features = Context.ServiceProvider.GetRequiredService<IEnumerable<IHttpFeature>>();
            foreach (var feature in features)
            {
                context.Features.Set(feature);
            }
            return next.Invoke(context);
        });
    }

    public override WebApplicationContext Context => _context;

    public WebApplication Use(Func<IHttpContext, WebApplicationMiddleware, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        Func<IHttpContext, WebApplicationMiddleware, Task> middleware2 = middleware;
        ((IWebApplicationPipelineBuilder)this).Use((WebApplicationMiddleware next) => (IHttpContext context) =>
        {
            return middleware2.Invoke(context, next);
        });
        return this;
    }
    IWebApplicationContext IWebApplication.Context => Context;
    IWebApplicationPipeline IWebApplicationPipelineBuilder.Build()
    {
        InvalidOperationException.ThrowIf(_isBuilt, "The web host is already built.");

        var middleware = new WebApplicationMiddleware(context =>
        {
            return Task.CompletedTask;
        });

        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            middleware = _middleware[i].Invoke(middleware);
        }

        return new WebApplicationPipeline(middleware);
    }
    IWebApplicationPipelineBuilder IWebApplicationPipelineBuilder.Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        return ((IWebApplicationPipelineBuilder)this).Use((WebApplicationMiddleware next) =>
        {
            return middleware.Invoke(Context, next);
        });
    }
    IWebApplicationPipelineBuilder IWebApplicationPipelineBuilder.Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _middleware.Add(middleware);

        return this;
    }
    IWebApplicationPipelineBuilder IWebApplicationPipelineBuilder.Use(IWebApplicationMiddleware middleware)
    {
        return (IWebApplicationPipelineBuilder)this.Use(middleware.InvokeAsync);
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
        ArgumentNullException.ThrowIfNull(options);

        return new WebApplicationBuilder(options);
    }
}
