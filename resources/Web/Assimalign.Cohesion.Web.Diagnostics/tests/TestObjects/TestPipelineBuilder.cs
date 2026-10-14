using System;

using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

/// <summary>
/// A minimal <see cref="IWebApplicationPipelineBuilder"/> that captures the middleware a pipeline verb
/// registers, so a test can compose verbs through their public surface (<c>UseHttpLogging</c>,
/// <c>UseForwardedHeaders</c>) and then drive the captured middleware directly over
/// <see cref="LoggingTestContext"/>. Only <see cref="Use(IWebApplicationMiddleware)"/> is functional;
/// the other overloads are inert.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    /// <summary>Gets the most recently registered middleware.</summary>
    public IWebApplicationMiddleware? LastMiddleware { get; private set; }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
    {
        LastMiddleware = middleware;
        return this;
    }

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware) => this;

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware) => this;

    public IWebApplicationPipeline Build() => throw new NotSupportedException();
}
