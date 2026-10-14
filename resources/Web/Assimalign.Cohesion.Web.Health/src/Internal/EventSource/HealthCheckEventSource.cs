using System;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Assimalign.Cohesion.Web.Health.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Health")]
internal sealed class HealthCheckEventSource : EventSource
{
    public static readonly HealthCheckEventSource Log = new();

    private long _failedChecks;
    private PollingCounter? _failedChecksCounter;

    private HealthCheckEventSource()
    {
    }

    internal long FailedChecks => Interlocked.Read(ref _failedChecks);

    [NonEvent]
    public void StatusChanged(string checkName, int previousStatus, HealthStatus status, string cause, Exception? exception)
    {
        if (previousStatus == (int)status)
        {
            return;
        }

        if (previousStatus == -1 ? status != HealthStatus.Healthy : (int)status < previousStatus)
        {
            if (IsEnabled(EventLevel.Warning, EventKeywords.None))
            {
                HealthCheckWorsened(checkName,
                    previousStatus == -1 ? "Unknown" : ((HealthStatus)previousStatus).ToString(),
                    status.ToString(), cause, exception?.GetType().FullName ?? string.Empty,
                    exception?.Message ?? string.Empty);
            }
        }
        else if (previousStatus != -1 && IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            HealthCheckImproved(checkName, ((HealthStatus)previousStatus).ToString(), status.ToString());
        }
    }

    [NonEvent]
    public void CheckFailed(HealthCheckRegistration registration, string cause, Exception exception)
    {
        Interlocked.Increment(ref _failedChecks);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            HealthCheckFailed(registration.Name, registration.FailureStatus.ToString(), cause,
                exception.GetType().FullName ?? string.Empty, exception.Message, registration.Timeout.TotalMilliseconds);
        }
    }

    [Event(1, Level = EventLevel.Warning, Message = "Health check {0} worsened from {1} to {2}: {3}; {4}: {5}")]
    private void HealthCheckWorsened(string checkName, string previousStatus, string status, string cause,
        string exceptionType, string exceptionMessage)
        => WriteEvent(1, checkName, previousStatus, status, cause, exceptionType, exceptionMessage);

    [Event(2, Level = EventLevel.Informational, Message = "Health check {0} improved from {1} to {2}")]
    private void HealthCheckImproved(string checkName, string previousStatus, string status)
        => WriteEvent(2, checkName, previousStatus, status);

    [Event(3, Level = EventLevel.Verbose, Message = "Health check {0} failed with status {1}: {2}; {3}: {4}; timeout {5} ms")]
    private void HealthCheckFailed(string checkName, string failureStatus, string cause, string exceptionType,
        string exceptionMessage, double timeoutMilliseconds)
        => WriteEvent(3, checkName, failureStatus, cause, exceptionType, exceptionMessage, timeoutMilliseconds);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _failedChecksCounter ??= new PollingCounter("failed-checks", this, () => FailedChecks)
            {
                DisplayName = "Failed health checks"
            };
        }
    }
}
