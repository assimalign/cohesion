using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Composes an application. The builder is the root application set: what the root gateway
/// deploys, with each child gateway's set below it.
/// </summary>
/// <remarks>
/// <see cref="Build"/> closes composition: afterwards no set in the hierarchy accepts a resource,
/// a gateway, or a dependency. Building needs no gateway, so a build step can produce the model and
/// its manifest without running anything.
/// </remarks>
public interface IApplicationBuilder : IApplicationSet
{
    /// <summary>
    /// Gets the command-line arguments the application was started with.
    /// </summary>
    IReadOnlyList<string> Args { get; }

    /// <summary>
    /// Selects the gateway that runs the application.
    /// </summary>
    /// <param name="gateway">The gateway.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="gateway"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// The builder already has a gateway, or has already built its application.
    /// </exception>
    IApplicationBuilder UseGateway(IApplicationGateway gateway);

    /// <summary>
    /// Closes composition and builds the application.
    /// </summary>
    /// <returns>The application.</returns>
    /// <exception cref="System.InvalidOperationException">The builder has already built its application.</exception>
    IApplication Build();
}
