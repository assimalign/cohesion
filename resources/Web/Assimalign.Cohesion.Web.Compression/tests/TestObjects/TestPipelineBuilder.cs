using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Compression.Tests.TestObjects;

/// <summary>
/// A minimal <see cref="IWebApplicationPipelineBuilder"/> over an application context whose
/// <see cref="IWebApplicationContext.Middleware"/> holds what a test registered, as
/// <c>builder.Services.AddResponseCompression(...)</c> or <c>AddRequestDecompression(...)</c> would. A
/// context-aware verb such as <c>UseResponseCompression()</c> composes over the terminal immediately, so a
/// test can invoke what the verb placed over <see cref="TestHttpContext"/>. The other overloads are inert.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly TestApplicationContext _context = new();
    private readonly WebApplicationMiddleware _terminal;

    /// <summary>Initializes a builder whose placed middleware calls <paramref name="terminal"/> as next.</summary>
    public TestPipelineBuilder(WebApplicationMiddleware? terminal = null)
    {
        _terminal = terminal ?? (static _ => Task.CompletedTask);
    }

    /// <summary>Gets the middleware the application registered.</summary>
    public List<IWebApplicationMiddleware> Registered => _context.Registered;

    /// <summary>Gets what the most recent context-aware verb composed over the terminal.</summary>
    public WebApplicationMiddleware? LastComposed { get; private set; }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware) => this;

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware) => this;

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        LastComposed = middleware.Invoke(_context, _terminal);
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
