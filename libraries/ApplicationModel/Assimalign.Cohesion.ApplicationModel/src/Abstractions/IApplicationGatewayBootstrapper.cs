using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Emits the selected gateway's own platform installation: the cohesion-system prerequisites
/// it needs before owning an application, rather than the application's compiled resources.
/// Writes the installation representation without contacting the target platform.
/// </summary>
public interface IApplicationGatewayBootstrapper
{
    /// <summary>Writes bootstrap resources for one or more application models.</summary>
    /// <param name="models">The application models that the gateway will own.</param>
    /// <param name="output">The destination for the platform bootstrap representation.</param>
    /// <param name="cancellationToken">Signals that bootstrap generation should be abandoned.</param>
    /// <returns>A task that completes after the bootstrap representation has been written.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="models"/> or <paramref name="output"/> is <see langword="null"/>.
    /// </exception>
    Task BootstrapAsync(
        IReadOnlyList<IApplicationModel> models,
        TextWriter output,
        CancellationToken cancellationToken = default);
}
