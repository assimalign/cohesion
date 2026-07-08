using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Health.Hosting.Internal;

/// <summary>
/// The periodic health-check publisher. Rides the Hosting execution menu as a
/// <see cref="BackgroundService"/>: after an initial delay it evaluates the registered checks
/// on a fixed interval and fans each report out to every registered
/// <see cref="IHealthPublisher"/>.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="BackgroundService"/> (not a <see cref="DedicatedThreadService"/>) because the
/// loop is asynchronous I/O paced by a <see cref="PeriodicTimer"/> — it must not own an OS
/// thread for its whole life.
/// </para>
/// <para>
/// Failure isolation is deliberate: a per-cycle evaluation timeout skips that cycle rather than
/// tearing down the loop, and a publisher that throws is swallowed so it neither kills the loop
/// nor blocks its siblings. Only host-driven cancellation ends the service.
/// </para>
/// </remarks>
internal sealed class HealthCheckPublisherService : BackgroundService
{
    private readonly IHealthCheckService _healthCheckService;
    private readonly IHealthPublisher[] _publishers;
    private readonly HealthCheckPublisherOptions _options;

    public HealthCheckPublisherService(
        IHealthCheckService healthCheckService,
        IEnumerable<IHealthPublisher> publishers,
        HealthCheckPublisherOptions options)
    {
        ArgumentNullException.ThrowIfNull(healthCheckService);
        ArgumentNullException.ThrowIfNull(publishers);
        ArgumentNullException.ThrowIfNull(options);

        _healthCheckService = healthCheckService;
        _publishers = publishers.ToArray();
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Nothing consumes the reports: don't waste probe cycles evaluating checks.
        if (_publishers.Length == 0)
        {
            return;
        }

        try
        {
            if (_options.Delay > TimeSpan.Zero)
            {
                await Task.Delay(_options.Delay, cancellationToken).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(_options.Period);

            // Publish immediately after the startup delay, then once per period.
            do
            {
                await PublishCycleAsync(cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: a clean stop.
        }
    }

    private async Task PublishCycleAsync(CancellationToken cancellationToken)
    {
        HealthReport report;

        using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            if (_options.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                timeoutSource.CancelAfter(_options.Timeout);
            }

            try
            {
                report = await _healthCheckService
                    .CheckHealthAsync(_options.Predicate, timeoutSource.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // This cycle exceeded its timeout budget: skip it and wait for the next tick.
                return;
            }
        }

        foreach (IHealthPublisher publisher in _publishers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await publisher.PublishAsync(report, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A failing publisher must not kill the loop or starve its siblings. The report is
                // still delivered to every other publisher; the next cycle retries this one.
            }
        }
    }
}
