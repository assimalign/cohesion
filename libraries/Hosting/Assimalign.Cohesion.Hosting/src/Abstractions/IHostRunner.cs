using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Defines a host run pipeline that can wrap the complete lifetime of a plain Cohesion host.
/// </summary>
public interface IHostRunner
{
    /// <summary>
    /// Runs the supplied host lifetime.
    /// </summary>
    /// <param name="run">The one-shot host run handle.</param>
    /// <param name="cancellationToken">Signals a shutdown request for this run.</param>
    /// <returns>A task that represents the complete wrapped host run.</returns>
    Task RunAsync(IHostRun run, CancellationToken cancellationToken = default);
}
