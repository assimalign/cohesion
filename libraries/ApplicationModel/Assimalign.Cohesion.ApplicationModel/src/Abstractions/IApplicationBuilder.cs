using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Fluently assembles an <see cref="IApplicationModel"/> and selects the gateway that
/// will realize it, then produces a runnable <see cref="IApplication"/>.
/// </summary>
public interface IApplicationBuilder
{
    /// <summary>
    /// Adds a resource to the model and returns its descriptor so dependency edges can
    /// be chained fluently (for example <c>builder.AddWebApp("admin").DependsOn(identity)</c>).
    /// </summary>
    /// <param name="resource">The resource to add.</param>
    /// <returns>The descriptor wrapping <paramref name="resource"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
    IApplicationResourceDescriptor AddResource(IApplicationResource resource);

    /// <summary>
    /// Adds a resource produced from the in-progress model, letting a resource read
    /// already-declared peers (for example to bind an endpoint to a dependency).
    /// </summary>
    /// <param name="configure">A factory that produces the resource from the current model.</param>
    /// <returns>The descriptor wrapping the produced resource.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    IApplicationResourceDescriptor AddResource(Func<IApplicationModel, IApplicationResource> configure);

    /// <summary>
    /// Selects the gateway that will realize the model. Required: <see cref="Build"/>
    /// throws when no gateway has been selected. Returns the builder for chaining.
    /// </summary>
    /// <param name="gateway">The gateway that will realize the model.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gateway"/> is <see langword="null"/>.</exception>
    IApplicationBuilder UseGateway(IApplicationGateway gateway);

    /// <summary>
    /// Validates the graph — unique resource names, no dependency cycles, all
    /// dependencies present, and a gateway selected — and returns the runnable application.
    /// </summary>
    /// <returns>The built application.</returns>
    /// <exception cref="InvalidOperationException">
    /// No gateway was selected, a resource name is duplicated, a dependency is missing,
    /// or the dependency graph contains a cycle.
    /// </exception>
    IApplication Build();
}
