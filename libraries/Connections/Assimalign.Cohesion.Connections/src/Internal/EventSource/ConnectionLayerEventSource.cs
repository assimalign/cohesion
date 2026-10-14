using System;
using System.Diagnostics.Tracing;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Connections.Internal;

/// <summary>
/// The diagnostics of layered listeners (<c>listener.Use(layer)</c>): an accepted connection whose
/// upgrade failed, and the upgrade counters.
/// </summary>
/// <remarks>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools enable
/// it by its assembly name, <c>Assimalign.Cohesion.Connections</c>; applications forward it into their
/// logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. The drivers report the connections
/// themselves; this source reports only what the layered listener decides: that an upgrade failed and its
/// connection was closed while the listener kept accepting. Every upgrade the listener starts reports its
/// start and its end exactly once, which keeps the <c>current-upgrades</c> gauge exact.
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Connections")]
internal sealed class ConnectionLayerEventSource : EventSource
{
    public static readonly ConnectionLayerEventSource Log = new();

    private PollingCounter? _currentUpgradesCounter;
    private PollingCounter? _failedUpgradesCounter;
    private long _currentUpgrades;
    private long _failedUpgrades;

    private ConnectionLayerEventSource()
    {
    }

    /// <summary>The upgrades started and not yet finished.</summary>
    internal long CurrentUpgrades => Volatile.Read(ref _currentUpgrades);

    /// <summary>The upgrades that failed since the process started.</summary>
    internal long FailedUpgrades => Volatile.Read(ref _failedUpgrades);

    [NonEvent]
    public void UpgradeStarted()
    {
        Interlocked.Increment(ref _currentUpgrades);
    }

    [NonEvent]
    public void UpgradeFinished()
    {
        Interlocked.Decrement(ref _currentUpgrades);
    }

    /// <summary>
    /// Reports an upgrade that failed or was canceled by its layer. The payload carries the connection's
    /// identity and the exception's type and message: never the bytes the peer sent, a certificate, or key
    /// material.
    /// </summary>
    [NonEvent]
    public void UpgradeFailed(ConnectionId connectionId, EndPoint? remoteEndPoint, Exception exception)
    {
        Interlocked.Increment(ref _failedUpgrades);

        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            UpgradeFailed(
                connectionId.ToString(),
                remoteEndPoint?.ToString() ?? string.Empty,
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message);
        }
    }

    [Event(1, Level = EventLevel.Warning, Message = "Connection {0} from {1} failed its upgrade and was closed: {2}: {3}")]
    private void UpgradeFailed(string connectionId, string remoteEndPoint, string exceptionType, string exceptionMessage)
        => WriteEvent(1, connectionId, remoteEndPoint, exceptionType, exceptionMessage);

    /// <inheritdoc />
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        // Created on the first enable command and kept for the source's lifetime; the backing fields are
        // maintained whether or not anyone listens, so a tool that attaches late still reads exact values.
        _currentUpgradesCounter ??= new PollingCounter("current-upgrades", this, () => Volatile.Read(ref _currentUpgrades))
        {
            DisplayName = "Current Upgrades",
        };
        _failedUpgradesCounter ??= new PollingCounter("failed-upgrades", this, () => Volatile.Read(ref _failedUpgrades))
        {
            DisplayName = "Failed Upgrades",
        };
    }
}
