using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Health.Tests;

/// <summary>
/// A configurable <see cref="IHealthCheck"/> test double: returns a fixed status, throws, or
/// blocks until cancelled (to exercise the per-check timeout path).
/// </summary>
internal sealed class StubCheck : IHealthCheck
{
    private readonly HealthStatus _status;
    private readonly Exception? _throws;
    private readonly bool _blockUntilCancelled;

    public StubCheck(HealthStatus status)
    {
        _status = status;
    }

    private StubCheck(Exception throws)
    {
        _throws = throws;
    }

    private StubCheck(bool blockUntilCancelled)
    {
        _blockUntilCancelled = blockUntilCancelled;
    }

    public int Invocations { get; private set; }

    public static StubCheck Throwing(Exception exception) => new(exception);

    public static StubCheck Blocking() => new(blockUntilCancelled: true);

    public async ValueTask<HealthCheckResult> CheckAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Invocations++;

        if (_throws is not null)
        {
            throw _throws;
        }

        if (_blockUntilCancelled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        return new HealthCheckResult(_status);
    }
}
