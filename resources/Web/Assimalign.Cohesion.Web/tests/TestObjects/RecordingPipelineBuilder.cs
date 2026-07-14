using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Tests.TestObjects;

/// <summary>
/// Minimal <see cref="IWebApplicationPipelineBuilder"/> stub that records the
/// registrations it receives so tests can inspect how extension members translate
/// their inputs onto the core builder surface.
/// </summary>
internal sealed class RecordingPipelineBuilder : IWebApplicationPipelineBuilder
{
    public List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> Registrations { get; } = [];

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
    {
        Registrations.Add(next => context => middleware.InvokeAsync(context, next));
        return this;
    }

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        Registrations.Add(middleware);
        return this;
    }

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        Registrations.Add(next => middleware.Invoke(null!, next));
        return this;
    }

    public IWebApplicationPipeline Build() => throw new NotSupportedException("The stub records registrations only.");
}
