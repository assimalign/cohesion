using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Health.Internal;

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
/// <para>
/// A thrown or timed-out check gets a fixed description, never one derived from the exception:
/// health endpoints are typically unauthenticated, and a dependency's exception message can carry
/// host, database or user names (CWE-209). The exception itself stays on the entry, for in-process
/// readers; server-side diagnostics report its type and message separately.
/// </para>
/// </remarks>
internal sealed class HealthCheckService : IHealthCheckService
{
    /// <summary>
    /// The description of an entry whose check threw.
    /// </summary>
    internal const string ThrewDescription = "The check threw an exception.";

    /// <summary>
    /// The description of an entry whose check exceeded its timeout.
    /// </summary>
    internal const string TimedOutDescription = "The check timed out.";

    private static readonly IReadOnlyDictionary<string, object> _emptyData =
        new Dictionary<string, object>(0);

    private readonly HealthCheckRegistration[] _registrations;
    private readonly int[] _lastStatuses;

    public HealthCheckService(HealthCheckRegistration[] registrations)
    {
        _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
        _lastStatuses = new int[registrations.Length];
        Array.Fill(_lastStatuses, -1); // Unknown: no probe has completed for this service's slot.
    }

    public async ValueTask<HealthReport> CheckHealthAsync(
        Func<HealthCheckRegistration, bool>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, HealthReportEntry>(StringComparer.OrdinalIgnoreCase);
        long start = Stopwatch.GetTimestamp();

        for (int index = 0; index < _registrations.Length; index++)
        {
            HealthCheckRegistration registration = _registrations[index];
            cancellationToken.ThrowIfCancellationRequested();

            if (predicate is not null && !predicate(registration))
            {
                continue;
            }

            (HealthReportEntry entry, string cause) = await RunCheckAsync(registration, cancellationToken).ConfigureAwait(false);
            int previousStatus = Interlocked.Exchange(ref _lastStatuses[index], (int)entry.Status);
            if (cause != "Reported")
            {
                HealthCheckEventSource.Log.CheckFailed(registration, cause, entry.Exception!);
            }
            HealthCheckEventSource.Log.StatusChanged(registration.Name, previousStatus, entry.Status, cause, entry.Exception);
            entries[registration.Name] = entry;
        }

        return new HealthReport(entries, Stopwatch.GetElapsedTime(start));
    }

    private static async ValueTask<(HealthReportEntry Entry, string Cause)> RunCheckAsync(
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

            return (new HealthReportEntry(
                result.Status,
                result.Description,
                Stopwatch.GetElapsedTime(start),
                result.Exception,
                result.Data,
                registration.Tags), "Reported");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-check timeout tripped (the caller's token is still live): fold the timeout
            // into this check's entry rather than failing the whole report. The cancellation
            // exception stays on the entry so an in-process reader can see where the check stopped.
            return (new HealthReportEntry(
                registration.FailureStatus,
                TimedOutDescription,
                Stopwatch.GetElapsedTime(start),
                exception,
                _emptyData,
                registration.Tags), "Timeout");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (new HealthReportEntry(
                registration.FailureStatus,
                ThrewDescription,
                Stopwatch.GetElapsedTime(start),
                exception,
                _emptyData,
                registration.Tags), "Exception");
        }
        finally
        {
            timeoutSource?.Dispose();
        }
    }
}
