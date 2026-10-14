using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Health.Tests;

/// <summary>
/// A configurable <see cref="IHealthCheck"/> test double: returns a fixed status, throws, or
/// blocks until cancelled (to exercise the per-check timeout path), optionally reporting the
/// cancellation with its own message, as a dependency client does when its connect times out.
/// </summary>
internal sealed class StubCheck : IHealthCheck
{
    private readonly HealthStatus _status;
    private readonly Exception? _throws;
    private readonly bool _blockUntilCancelled;
    private readonly string? _cancellationMessage;

    public StubCheck(HealthStatus status)
    {
        _status = status;
    }

    private StubCheck(Exception throws)
    {
        _throws = throws;
    }

    private StubCheck(bool blockUntilCancelled, string? cancellationMessage)
    {
        _blockUntilCancelled = blockUntilCancelled;
        _cancellationMessage = cancellationMessage;
    }

    public int Invocations { get; private set; }

    public static StubCheck Throwing(Exception exception) => new(exception);

    public static StubCheck Blocking() => new(blockUntilCancelled: true, cancellationMessage: null);

    public static StubCheck BlockingThenCancelledWith(string message) => new(blockUntilCancelled: true, message);

    public async ValueTask<HealthCheckResult> CheckAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Invocations++;

        if (_throws is not null)
        {
            throw _throws;
        }

        if (_blockUntilCancelled)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (_cancellationMessage is not null)
            {
                throw new OperationCanceledException(_cancellationMessage, exception, cancellationToken);
            }
        }

        return new HealthCheckResult(_status);
    }
}
