using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Internal;

/// <summary>
/// Collects the middleware of a pipeline branch (<c>Map</c>, <c>MapWhen</c>, <c>UseWhen</c>). The
/// pipeline that contains the branch composes it when that pipeline is built, with the application
/// context and the branch's terminal.
/// </summary>
/// <remarks>
/// Every registration is kept in the component-factory shape that takes the application context, so a
/// middleware that needs the context at composition time (<c>UseStaticFiles</c> reads the web root,
/// <c>UseRouting</c> builds the router) composes inside a branch exactly as it does on the
/// application. The branch has no context of its own until its parent composes it.
/// </remarks>
internal sealed class WebApplicationBranchBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();

    /// <inheritdoc />
    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _middleware.Add((_, next) => context => middleware.InvokeAsync(context, next));
        return this;
    }

    /// <inheritdoc />
    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _middleware.Add((_, next) => middleware(next));
        return this;
    }

    /// <inheritdoc />
    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _middleware.Add(middleware);
        return this;
    }

    /// <summary>
    /// A branch has no pipeline of its own: the pipeline that contains it composes it.
    /// </summary>
    /// <returns>Never returns.</returns>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public IWebApplicationPipeline Build() => throw new InvalidOperationException(
        "A pipeline branch is composed by the pipeline that contains it, when that pipeline is built; it cannot be built on its own.");

    /// <summary>
    /// Composes the branch's middleware around <paramref name="terminal"/>, last registration innermost,
    /// invoking each component factory once.
    /// </summary>
    /// <param name="application">The application context the containing pipeline composes with.</param>
    /// <param name="terminal">
    /// The branch's terminal: the standard terminal for a branch that does not rejoin, or the containing
    /// pipeline's <c>next</c> for one that does.
    /// </param>
    /// <returns>The composed branch.</returns>
    public WebApplicationMiddleware Compose(IWebApplicationContext application, WebApplicationMiddleware terminal)
    {
        WebApplicationMiddleware pipeline = terminal;

        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i](application, pipeline);
        }

        return pipeline;
    }
}
