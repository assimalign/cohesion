using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

/// <summary>
/// A minimal <see cref="IWebApplicationPipelineBuilder"/> over an application context whose
/// <see cref="IWebApplicationContext.Middleware"/> holds what a test registered, as
/// <c>builder.Services.AddOutputCache(...)</c> would. A context-aware verb such as <c>UseOutputCache()</c>
/// composes over <see cref="Terminal"/> immediately, so a test can invoke what the verb placed.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly TestApplicationContext _context = new();

    /// <summary>Gets the middleware the application registered.</summary>
    public List<IWebApplicationMiddleware> Registered => _context.Registered;

    /// <summary>
    /// Gets or sets the next a context-aware verb composes over: the endpoint behind the placed
    /// middleware. It completes at once unless a test replaces it.
    /// </summary>
    public WebApplicationMiddleware Terminal { get; set; } = static _ => Task.CompletedTask;

    /// <summary>Gets what the most recent context-aware verb composed over <see cref="Terminal"/>.</summary>
    public WebApplicationMiddleware? LastComposed { get; private set; }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware) => this;

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware) => this;

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        LastComposed = middleware.Invoke(_context, Terminal);
        return this;
    }

    public IWebApplicationPipeline Build() => throw new NotSupportedException();

    private sealed class TestApplicationContext : IWebApplicationContext
    {
        public List<IWebApplicationMiddleware> Registered { get; } = [];

        public FileSystemPath? ContentRootPath => null;

        public FileSystemPath? WebRootPath => null;

        public IEnumerable<IWebApplicationMiddleware> Middleware => Registered;

        public IEnumerable<IWebApplicationServer> Servers => [];

        public IEnumerable<IHttpFeature> Features => [];

        public IEnumerable<IWebApplicationSetting> Settings => [];
    }
}
