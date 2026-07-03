namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// The lifecycle state of an <see cref="IHost"/>.
/// </summary>
public enum HostState
{
    /// <summary>
    /// The host has never been started.
    /// </summary>
    Idle = 0,

    /// <summary>
    /// The host is starting its hosted services.
    /// </summary>
    Starting,

    /// <summary>
    /// The host has started every hosted service and is running.
    /// </summary>
    Started,

    /// <summary>
    /// An alias of <see cref="Started"/>.
    /// </summary>
    Running = Started,

    /// <summary>
    /// The host is stopping its hosted services.
    /// </summary>
    Stopping,

    /// <summary>
    /// The host stopped cleanly. A stopped host can be started again.
    /// </summary>
    Stopped,

    /// <summary>
    /// The host failed to start: partially-started services were rolled back best-effort
    /// and the original failure was rethrown to the caller. Distinct from a clean
    /// <see cref="Stopped"/>. A failed host can be disposed, stopped (a no-op), or
    /// started again.
    /// </summary>
    Failed,
}
