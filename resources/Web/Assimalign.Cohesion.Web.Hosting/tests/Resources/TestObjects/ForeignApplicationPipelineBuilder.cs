using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

internal sealed class ForeignApplicationPipelineBuilder : IWebApplicationPipelineBuilder, IWebApplicationContext
{
    private Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware>? _factory;

    public FileSystemPath? ContentRootPath => null;
    public FileSystemPath? WebRootPath => null;
    public IEnumerable<IWebApplicationMiddleware> Middleware => [];
    public IEnumerable<IWebApplicationServer> Servers => [];
    public IEnumerable<IHttpFeature> Features => [];

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware) => throw new NotSupportedException();

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        => throw new NotSupportedException();

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        _factory = middleware;
        return this;
    }

    public IWebApplicationPipeline Build()
    {
        _ = _factory!(this, static _ => Task.CompletedTask);
        throw new InvalidOperationException("The managed middleware must reject this application context.");
    }
}
