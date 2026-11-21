using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Records the upgrade failures a layered listener reports, which is how an AMQP listener reports a
/// connection that failed establishment: through the <c>Assimalign.Cohesion.Connections</c> event source,
/// enabled by name because its type is internal to that assembly.
/// </summary>
/// <remarks>
/// The event source is process-wide, so failures are matched by the carrier's connection identifier and
/// tests that use the recorder run in <see cref="AmqpHeaderPhaseCollection"/>.
/// </remarks>
internal sealed class AmqpUpgradeFailureRecorder : EventListener
{
    private const string sourceName = "Assimalign.Cohesion.Connections";

    // Field initializers run before the base constructor, which may already report existing sources.
    private readonly ConcurrentQueue<(string ConnectionId, string ExceptionType, string ExceptionMessage)> _failures = new();

    /// <summary>
    /// Waits for the failure reported for the connection and returns its exception's type name and message.
    /// </summary>
    public async Task<(string ExceptionType, string ExceptionMessage)> WaitForFailureAsync(ConnectionId connectionId, CancellationToken cancellationToken)
    {
        string id = connectionId.ToString();

        while (true)
        {
            foreach ((string ConnectionId, string ExceptionType, string ExceptionMessage) failure in _failures)
            {
                if (string.Equals(failure.ConnectionId, id, StringComparison.Ordinal))
                {
                    return (failure.ExceptionType, failure.ExceptionMessage);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    /// <inheritdoc />
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (string.Equals(eventSource.Name, sourceName, StringComparison.Ordinal))
        {
            EnableEvents(eventSource, EventLevel.Warning);
        }
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (string.Equals(eventData.EventSource.Name, sourceName, StringComparison.Ordinal)
            && string.Equals(eventData.EventName, "UpgradeFailed", StringComparison.Ordinal)
            && eventData.Payload is [string connectionId, _, string exceptionType, string exceptionMessage])
        {
            _failures.Enqueue((connectionId, exceptionType, exceptionMessage));
        }
    }
}
