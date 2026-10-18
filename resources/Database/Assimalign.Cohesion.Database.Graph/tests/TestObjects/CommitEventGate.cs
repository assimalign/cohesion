using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

// Mirrors Documents' gate in db1a2e3bf: TransactionCommitted fires after writer-lock
// release and before coordinator untracking. No shipped visibility boundary changes.
internal sealed class CommitEventGate : EventListener
{
    private readonly string _database;
    private readonly long _sequence;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;
    private int _timedOut;

    internal CommitEventGate(string database, ulong sequence)
    {
        _database = database;
        _sequence = checked((long)sequence);
    }

    internal Task Entered => _entered.Task;
    internal int Calls => Volatile.Read(ref _calls);
    internal bool TimedOut => Volatile.Read(ref _timedOut) != 0;
    internal void Release() => _released.TrySetResult();

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Assimalign.Cohesion.Database.Transactions")
        { EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All); }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId != 2 || eventData.Payload is not { Count: >= 2 } payload ||
            payload[0] is not string database || database != _database ||
            payload[1] is not long sequence || sequence != _sequence) { return; }
        Interlocked.Increment(ref _calls);
        _entered.TrySetResult();
        if (!_released.Task.Wait(TimeSpan.FromSeconds(10))) { Interlocked.Exchange(ref _timedOut, 1); }
    }
}
