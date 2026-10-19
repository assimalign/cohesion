using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

internal sealed class LockWaitListener : EventListener
{
    private readonly ConcurrentDictionary<long, bool> _waiting = new();

    public async Task WaitForWaitAsync(TransactionSequence owner, CancellationToken cancellationToken)
    {
        while (!_waiting.ContainsKey((long)owner.Value))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Assimalign.Cohesion.Database.Transactions")
        {
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x2);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs written)
    {
        if (written.EventName == "LockWaitStart" && written.Payload is { Count: > 1 } payload)
        {
            _waiting[(long)payload[1]!] = true;
        }
    }
}
