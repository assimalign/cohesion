using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Health.Internal;

/// <summary>
/// The default <see cref="IHealthCheckService"/>. Runs an immutable set of registrations
/// sequentially, applying each check's per-check timeout and translating a thrown check or a
/// timeout into the registration's failure status.
/// </summary>
/// <remarks>
/// Checks run one at a time. Health checks are expected to be cheap and few; sequential
/// execution keeps ordering deterministic and avoids a burst of concurrent probes against
/// shared dependencies. Caller cancellation (host shutdown / request abort) propagates out;
/// a per-check timeout does not — it is folded into that check's entry so one slow dependency
/// never fails the whole report with an exception.
/// </remarks>
internal sealed class HealthCheckService : IHealthCheckService
{
    private static readonly IReadOnlyDictionary<string, object> EmptyData =
        new Dictionary<string, object>(0);

    private readonly HealthCheckRegistration[] _registrations;

    public HealthCheckService(HealthCheckRegistration[] registrations)
    {
        _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
    }

    public async ValueTask<HealthReport> CheckHealthAsync(
        Func<HealthCheckRegistration, bool>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, HealthReportEntry>(StringComparer.OrdinalIgnoreCase);
        long start = Stopwatch.GetTimestamp();

        foreach (HealthCheckRegistration registration in _registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (predicate is not null && !predicate(registration))
            {
                continue;
            }

            entries[registration.Name] = await RunCheckAsync(registration, cancellationToken).ConfigureAwait(false);
        }

        return new HealthReport(entries, Stopwatch.GetElapsedTime(start));
    }

    private static async ValueTask<HealthReportEntry> RunCheckAsync(
        HealthCheckRegistration registration,
        CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        CancellationTokenSource? timeoutSource = null;

        try
        {
            CancellationToken checkToken = cancellationToken;

            if (registration.Timeout != HealthCheckRegistration.InfiniteTimeout)
            {
                timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(registration.Timeout);
                checkToken = timeoutSource.Token;
            }

            var context = new HealthCheckContext(registration);
            IHealthCheck check = registration.Factory();
            HealthCheckResult result = await check.CheckAsync(context, checkToken).ConfigureAwait(false);

            return new HealthReportEntry(
                result.Status,
                result.Description,
                Stopwatch.GetElapsedTime(start),
                result.Exception,
                result.Data,
                registration.Tags);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-check timeout tripped (the caller's token is still live): fold the timeout
            // into this check's entry rather than failing the whole report.
            return new HealthReportEntry(
                registration.FailureStatus,
                $"The health check '{registration.Name}' timed out after {registration.Timeout}.",
                Stopwatch.GetElapsedTime(start),
                exception: null,
                EmptyData,
                registration.Tags);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HealthReportEntry(
                registration.FailureStatus,
                exception.Message,
                Stopwatch.GetElapsedTime(start),
                exception,
                EmptyData,
                registration.Tags);
        }
        finally
        {
            timeoutSource?.Dispose();
        }
    }
}
