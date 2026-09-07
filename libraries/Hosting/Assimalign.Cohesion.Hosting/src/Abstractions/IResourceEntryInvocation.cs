using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Represents one invocation of an enabled resource executable's registered entry point.
/// </summary>
public interface IResourceEntryInvocation
{
    /// <summary>
    /// Gets the host surrendered by the area builder for this invocation.
    /// </summary>
    /// <remarks>
    /// The task completes when the resource calls <see cref="ResourceRuntime.HostBuilt"/>.
    /// It faults when the entry point exits before constructing a host.
    /// </remarks>
    Task<IHost> HostReady { get; }

    /// <summary>
    /// Gets the task that completes when the resource entry point exits.
    /// </summary>
    Task Completion { get; }
}
