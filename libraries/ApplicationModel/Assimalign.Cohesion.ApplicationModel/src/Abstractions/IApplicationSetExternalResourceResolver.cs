using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Resolves an external directly from a sibling model supervised by the same application-set
/// gateway session.
/// </summary>
public interface IApplicationSetExternalResourceResolver
{
    /// <summary>Attempts to resolve an external from the supplied sibling models.</summary>
    /// <param name="models">The complete application-set model collection.</param>
    /// <param name="context">The external declaration and optional remote control-plane client.</param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>The direct resolution, or an unresolved result when the sibling is not observable.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="models"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    ValueTask<ExternalResourceResolution> ResolveInSetAsync(
        IReadOnlyList<IApplicationModel> models,
        ExternalResourceResolutionContext context,
        CancellationToken cancellationToken = default);
}
