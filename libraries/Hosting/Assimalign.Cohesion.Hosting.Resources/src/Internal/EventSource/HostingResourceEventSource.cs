using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

[EventSource(Name = "Assimalign.Cohesion.Hosting.Resources")]
internal sealed class HostingResourceEventSource : EventSource
{
    public static readonly HostingResourceEventSource Log = new();

    private readonly object _contributorThrottle = new();
    private long _contributorFailures;
    private long _nextContributorFailure;
    private int _suppressedContributorFailures;
    private PollingCounter? _contributorFailuresCounter;

    private HostingResourceEventSource()
    {
    }

    internal long ContributorFailures => Interlocked.Read(ref _contributorFailures);

    [NonEvent]
    public void ContributorFailed(string contributorName, Exception exception)
    {
        Interlocked.Increment(ref _contributorFailures);
        if (!IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            return;
        }

        int suppressedCount;
        lock (_contributorThrottle)
        {
            long now = Stopwatch.GetTimestamp();
            if (now < _nextContributorFailure)
            {
                if (_suppressedContributorFailures < int.MaxValue)
                {
                    _suppressedContributorFailures++;
                }
                return;
            }

            _nextContributorFailure = now + Stopwatch.Frequency;
            suppressedCount = _suppressedContributorFailures;
            _suppressedContributorFailures = 0;
        }

        ContributorFailed(
            contributorName,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            suppressedCount);
    }

    [Event(1, Level = EventLevel.Error, Message = "Health contributor {0} failed: {1}: {2}; suppressed {3}")]
    private void ContributorFailed(string contributorName, string exceptionType, string exceptionMessage, int suppressedCount)
        => WriteEvent(1, contributorName, exceptionType, exceptionMessage, suppressedCount);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _contributorFailuresCounter ??= new PollingCounter("contributor-failures", this, () => ContributorFailures)
            {
                DisplayName = "Contributor Failures",
            };
        }
    }
}
