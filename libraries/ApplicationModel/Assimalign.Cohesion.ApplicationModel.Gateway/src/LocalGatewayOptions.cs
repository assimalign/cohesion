using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Options controlling how the <see cref="LocalGateway"/> resolves, starts, probes, and stops
/// child processes.
/// </summary>
public sealed class LocalGatewayOptions
{
    /// <summary>
    /// The directory searched for a resource's executable. Defaults to the orchestrator's
    /// <see cref="AppContext.BaseDirectory"/> when <see langword="null"/>.
    /// </summary>
    public string? BaseDirectory { get; set; }

    /// <summary>
    /// A stdout substring that marks a child process as ready. When <see langword="null"/>,
    /// readiness is inferred from the process surviving <see cref="ReadySettle"/> without exiting.
    /// </summary>
    public string? ReadyMarker { get; set; }

    /// <summary>
    /// How long a process must stay alive to be considered ready when <see cref="ReadyMarker"/>
    /// is not set. Defaults to 750&#160;ms.
    /// </summary>
    public TimeSpan ReadySettle { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// The maximum time to wait for a resource to become ready before treating startup as failed.
    /// Defaults to 60&#160;seconds.
    /// </summary>
    public TimeSpan ReadinessBudget { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to wait for a child process to exit during shutdown before it is force-killed.
    /// Defaults to 10&#160;seconds.
    /// </summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(10);
}
