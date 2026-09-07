using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Evaluates one named resource health contribution.
/// </summary>
/// <param name="cancellationToken">Cancels the health evaluation.</param>
/// <returns>The current health contribution.</returns>
public delegate ValueTask<HealthContribution> ResourceHealthCheck(
    CancellationToken cancellationToken = default);
