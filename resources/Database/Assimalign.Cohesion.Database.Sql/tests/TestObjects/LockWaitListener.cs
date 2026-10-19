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

    // Invoked outside the lock table's lock, so a regression can gate a completed grant.
    public Action<EventWrittenEventArgs>? Observed { get; set; }

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
        if (_database is not null && written.Payload is { Count: > 1 } payload && Equals(payload[0], _database))
        {
            if (written.EventName == "LockWaitStart") { _waiting[(long)payload[1]!] = true; }
            Observed?.Invoke(written);
        }
    }
}
