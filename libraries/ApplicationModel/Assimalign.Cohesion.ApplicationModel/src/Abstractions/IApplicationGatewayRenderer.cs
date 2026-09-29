using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Compiles application models into the selected gateway's textual platform representation
/// without contacting the target platform.
/// </summary>
public interface IApplicationGatewayRenderer
{
    /// <summary>Renders one or more application models in declaration order.</summary>
    /// <param name="models">The application models to compile.</param>
    /// <param name="output">The destination for the compiled representation.</param>
    /// <param name="cancellationToken">Signals that rendering should be abandoned.</param>
    /// <returns>A task that completes after the representation has been written.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="models"/> or <paramref name="output"/> is <see langword="null"/>.
    /// </exception>
    Task RenderAsync(
        IReadOnlyList<IApplicationModel> models,
        TextWriter output,
        CancellationToken cancellationToken = default);
}
