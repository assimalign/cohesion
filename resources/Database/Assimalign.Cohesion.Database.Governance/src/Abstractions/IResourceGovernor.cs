using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Governance;

/// <summary>
/// Enforces resource budgets and quotas for database operations.
/// </summary>
public interface IResourceGovernor
{
    /// <summary>
    /// Attempts to admit work under configured resource budgets.
    /// </summary>
    /// <param name="workloadClass">Workload class identifier.</param>
    /// <param name="estimatedCost">Estimated cost units.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when admitted.</returns>
    ValueTask<bool> TryAdmitAsync(string workloadClass, double estimatedCost, CancellationToken cancellationToken = default);
}
