using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

[EventSource(Name = "Assimalign.Cohesion.Hosting.Resources")]
internal sealed class HostingResourceEventSource : EventSource
{
    public static readonly HostingResourceEventSource Log = new();

    private readonly object _throttleSync = new();
    private long _contributorFailures;
    private long _commandsApplied;
    private long _unknownCommandKinds;
    private long _commandConflicts;
    private long _commandHandlerFailures;
    private long _nextContributorFailure;
    private long _nextUnknownCommandKind;
    private long _nextCommandConflict;
    private long _nextCommandHandlerFailure;
    private int _suppressedContributorFailures;
    private int _suppressedUnknownCommandKinds;
    private int _suppressedCommandConflicts;
    private int _suppressedCommandHandlerFailures;
    private PollingCounter? _contributorFailuresCounter;
    private PollingCounter? _commandsAppliedCounter;
    private PollingCounter? _unknownCommandKindsCounter;
    private PollingCounter? _commandConflictsCounter;
    private PollingCounter? _commandHandlerFailuresCounter;

    private HostingResourceEventSource()
    {
    }

    internal long ContributorFailures => Interlocked.Read(ref _contributorFailures);
    internal long CommandsApplied => Interlocked.Read(ref _commandsApplied);
    internal long UnknownCommandKinds => Interlocked.Read(ref _unknownCommandKinds);
    internal long CommandConflicts => Interlocked.Read(ref _commandConflicts);
    internal long CommandHandlerFailures => Interlocked.Read(ref _commandHandlerFailures);

    [NonEvent]
    public void ContributorFailed(string contributorName, Exception exception)
    {
        Interlocked.Increment(ref _contributorFailures);
        if (IsEnabled(EventLevel.Error, EventKeywords.None) &&
            TryEmit(ref _nextContributorFailure, ref _suppressedContributorFailures, out int suppressedCount))
        {
            string exceptionMessage;
            try
            {
                exceptionMessage = exception.Message;
            }
            catch (Exception)
            {
                // Message is virtual: application diagnostics must not replace the health result.
                exceptionMessage = "The exception message could not be read.";
            }

            ContributorFailed(
                contributorName,
                exception.GetType().FullName ?? exception.GetType().Name,
                exceptionMessage,
                suppressedCount);
        }
    }

    [NonEvent]
    public void CommandApplied(string kind, bool delete)
    {
        Interlocked.Increment(ref _commandsApplied);
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            CommandApplied(kind, delete ? "Delete" : "Apply");
        }
    }

    [NonEvent]
    public void CommandUnknownKind(bool delete)
    {
        Interlocked.Increment(ref _unknownCommandKinds);
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) &&
            TryEmit(ref _nextUnknownCommandKind, ref _suppressedUnknownCommandKinds, out int suppressedCount))
        {
            // Unregistered input is never a diagnostic kind, even when a resource advertised it.
            CommandUnknownKind(string.Empty, delete ? "Delete" : "Apply", suppressedCount);
        }
    }

    [NonEvent]
    public void CommandConflict(string kind, bool delete)
    {
        Interlocked.Increment(ref _commandConflicts);
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) &&
            TryEmit(ref _nextCommandConflict, ref _suppressedCommandConflicts, out int suppressedCount))
        {
            CommandConflict(kind, delete ? "Delete" : "Apply", suppressedCount);
        }
    }

    [NonEvent]
    public void CommandHandlerFailed(string kind, bool delete, Exception exception)
    {
        Interlocked.Increment(ref _commandHandlerFailures);
        if (IsEnabled(EventLevel.Error, EventKeywords.None) &&
            TryEmit(ref _nextCommandHandlerFailure, ref _suppressedCommandHandlerFailures, out int suppressedCount))
        {
            CommandHandlerFailed(kind, delete ? "Delete" : "Apply",
                exception.GetType().FullName ?? exception.GetType().Name, suppressedCount);
        }
    }

    [Event(1, Level = EventLevel.Error, Message = "Health contributor {0} failed: {1}: {2}; suppressed {3}")]
    private void ContributorFailed(string contributorName, string exceptionType, string exceptionMessage, int suppressedCount)
        => WriteEvent(1, contributorName, exceptionType, exceptionMessage, suppressedCount);

    [Event(2, Level = EventLevel.Informational, Message = "Command {0} completed {1}")]
    private void CommandApplied(string kind, string operation)
        => WriteEvent(2, kind, operation);

    [Event(3, Level = EventLevel.Warning, Message = "Command kind was not registered for {1}; suppressed {2}")]
    private void CommandUnknownKind(string kind, string operation, int suppressedCount)
        => WriteEvent(3, kind, operation, suppressedCount);

    [Event(4, Level = EventLevel.Warning, Message = "Command {0} conflicted during {1}; suppressed {2}")]
    private void CommandConflict(string kind, string operation, int suppressedCount)
        => WriteEvent(4, kind, operation, suppressedCount);

    [Event(5, Level = EventLevel.Error, Message = "Command {0} failed during {1}: {2}; suppressed {3}")]
    private void CommandHandlerFailed(string kind, string operation, string exceptionType, int suppressedCount)
        => WriteEvent(5, kind, operation, exceptionType, suppressedCount);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _contributorFailuresCounter ??= new PollingCounter("contributor-failures", this, () => ContributorFailures)
            {
                DisplayName = "Contributor Failures",
            };
            _commandsAppliedCounter ??= new PollingCounter("commands-applied", this, () => CommandsApplied)
            {
                DisplayName = "Commands Applied",
            };
            _unknownCommandKindsCounter ??= new PollingCounter("unknown-command-kinds", this, () => UnknownCommandKinds)
            {
                DisplayName = "Unknown Command Kinds",
            };
            _commandConflictsCounter ??= new PollingCounter("command-conflicts", this, () => CommandConflicts)
            {
                DisplayName = "Command Conflicts",
            };
            _commandHandlerFailuresCounter ??= new PollingCounter("command-handler-failures", this, () => CommandHandlerFailures)
            {
                DisplayName = "Command Handler Failures",
            };
        }
    }

    [NonEvent]
    private bool TryEmit(ref long nextEmission, ref int suppressed, out int suppressedCount)
    {
        lock (_throttleSync)
        {
            long now = Stopwatch.GetTimestamp();
            if (now < nextEmission)
            {
                if (suppressed < int.MaxValue)
                {
                    suppressed++;
                }
                suppressedCount = 0;
                return false;
            }

            nextEmission = now + Stopwatch.Frequency;
            suppressedCount = suppressed;
            suppressed = 0;
            return true;
        }
    }
}
