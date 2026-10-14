using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// A built web application: its context, its request pipeline and the servers that feed it, started and
/// stopped as one unit.
/// </summary>
/// <remarks>
/// <see cref="IWebApplicationBuilder.Build"/> produces the application; <see cref="StartAsync"/> starts
/// its servers, which begin passing requests to the pipeline, and <see cref="StopAsync"/> stops them.
/// </remarks>
public interface IWebApplication
{
    /// <summary>
    /// Gets the application's context, which carries the features registered on the builder.
    /// </summary>
    IWebApplicationContext Context { get; }

    /// <summary>
    /// Starts the application's servers in registration order, after which they accept requests.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the start.</param>
    /// <returns>A task that completes when every server has started.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the application's servers in reverse registration order.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels a graceful stop.</param>
    /// <returns>A task that completes when every server has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
