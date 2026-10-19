using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
/// <summary>
/// Records the transactions of one database whose lock requests queued, from the transaction
/// kernel's <c>LockWaitStart</c> events, so a test can order requests on one queue.
/// </summary>
internal sealed class LockWaitListener : EventListener
{
    private readonly ConcurrentDictionary<long, bool> _waiting = new();
    private readonly string? _database;

    public LockWaitListener(string database)
    {
        _database = database;
    }

    public async Task WaitForWaitAsync(TransactionSequence sequence, CancellationToken cancellationToken)
    {
        while (!_waiting.ContainsKey((long)sequence.Value))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Assimalign.Cohesion.Database.Transactions")
        {
            // The Locks keyword.
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x2);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs written)
    {
        if (written.EventName == "LockWaitStart" && _database is not null && written.Payload is { Count: > 1 } payload && Equals(payload[0], _database))
        {
            _waiting[(long)payload[1]!] = true;
        }
    }
}
