using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Obtains an application model through that application's own describe or export control plane.
/// </summary>
public interface IApplicationModelResolver
{
    /// <summary>Resolves a member model for an application-set invocation.</summary>
    /// <param name="context">The current application-set gateway context.</param>
    /// <param name="cancellationToken">Signals that model discovery should be abandoned.</param>
    /// <returns>The member model to reconcile.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    ValueTask<IApplicationModel> ResolveAsync(
        ApplicationModelResolutionContext context,
        CancellationToken cancellationToken = default);
}
