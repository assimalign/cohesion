using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Resolves an application-boundary resource into its currently observed endpoints.
/// </summary>
public interface IExternalResourceResolver
{
    /// <summary>Resolves one external declaration without realizing it in this gateway.</summary>
    /// <param name="context">The declaration and available control-plane services.</param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>The current resolution, including its exported manifest identity when known.</returns>
    ValueTask<ExternalResourceResolution> ResolveAsync(
        ExternalResourceResolutionContext context,
        CancellationToken cancellationToken = default);
}
