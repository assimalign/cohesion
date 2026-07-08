using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Health.Internal;

/// <summary>
/// Adapts an inline probe delegate to the <see cref="IHealthCheck"/> contract so callers can
/// register a check with a lambda instead of a dedicated type.
/// </summary>
internal sealed class DelegateHealthCheck : IHealthCheck
{
    private readonly Func<HealthCheckContext, CancellationToken, ValueTask<HealthCheckResult>> _probe;

    public DelegateHealthCheck(Func<HealthCheckContext, CancellationToken, ValueTask<HealthCheckResult>> probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public ValueTask<HealthCheckResult> CheckAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => _probe(context, cancellationToken);
}
