using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Represents one invocation of an enabled resource executable's registered entry point.
/// </summary>
public interface IResourceEntryInvocation
{
    /// <summary>
    /// Gets the host surrendered by the area builder for this invocation.
    /// </summary>
    /// <remarks>
    /// The task completes after the resource calls <see cref="ResourceRuntime.HostBuilt"/> and
    /// invokes the surrendered host's run pipeline. It also completes when the entry point exits
    /// successfully with the surrendered host still idle, allowing the parent to start it. This
    /// keeps synchronous host composition after build inside the resource's ownership boundary.
    /// It faults when the entry point exits without surrendering a host. When the entry point
    /// fails after surrendering a host but before invoking its run pipeline, this task returns that
    /// host for deterministic cleanup while <see cref="Completion"/> retains the entry failure.
    /// </remarks>
    Task<IHost> HostReady { get; }

    /// <summary>
    /// Gets the task that completes when the resource entry point exits.
    /// </summary>
    /// <remarks>
    /// A classified resource failure faults the task with
    /// <see cref="ResourceEntryExitException"/> so an in-process supervisor can apply the same
    /// <c>cohesion/sysexits/v1</c> policy as an operating-system process supervisor.
    /// </remarks>
    Task Completion { get; }
}
