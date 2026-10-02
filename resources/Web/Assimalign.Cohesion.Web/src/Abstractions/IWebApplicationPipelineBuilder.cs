using System;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Composes a web application's request pipeline from middleware.
/// </summary>
public interface IWebApplicationPipelineBuilder
{
    /// <summary>
    /// Adds a middleware component to the pipeline.
    /// </summary>
    /// <param name="middleware">The middleware invoked for each request.</param>
    /// <returns>The same builder for chaining.</returns>
    IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware);

    /// <summary>
    /// Adds a middleware factory to the pipeline.
    /// </summary>
    /// <remarks>
    /// The factory is invoked when the pipeline is built (<see cref="Build"/>), not per request: it
    /// receives the next middleware in the chain and returns the delegate that runs for each request.
    /// Work that belongs to composition rather than to a request, such as validating configuration or
    /// building lookup tables, belongs in the factory body, so its failures surface when the pipeline
    /// is built.
    /// </remarks>
    /// <param name="middleware">The factory that creates the request delegate from the next middleware.</param>
    /// <returns>The same builder for chaining.</returns>
    IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware);

    /// <summary>
    /// Adds a middleware factory that also receives the application context.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Use(Func{WebApplicationMiddleware, WebApplicationMiddleware})"/>, the factory is
    /// invoked when the pipeline is built, not per request.
    /// </remarks>
    /// <param name="middleware">
    /// The factory that creates the request delegate from the application context and the next middleware.
    /// </param>
    /// <returns>The same builder for chaining.</returns>
    IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware);

    /// <summary>
    /// Builds the request pipeline from the registered middleware; requests pass through the
    /// middleware in registration order.
    /// </summary>
    /// <remarks>
    /// Every registered middleware factory is invoked here. The Web host builds the pipeline once,
    /// when the application starts, so register middleware before starting it.
    /// </remarks>
    /// <returns>The composed request pipeline.</returns>
    IWebApplicationPipeline Build();
}
