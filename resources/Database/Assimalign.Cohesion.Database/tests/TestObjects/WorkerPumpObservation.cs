using System;
using System.Runtime.CompilerServices;
using System.Threading;

// Deviates from namespace-matches-assembly: linked test support keeps its owning namespace.
namespace Assimalign.Cohesion.Database.Tests;

// Static accessors keep this test-only connection AOT-safe without a foreign visibility grant.
internal sealed class WorkerPumpObservation : IDisposable
{
    private readonly DatabaseEngineWorker _worker;
    private readonly Action<TimeSpan> _delay;
    private readonly Action<TimeSpan>? _previousDelay;
    private readonly Action<TimeSpan> _trigger;
    private readonly Action<TimeSpan>? _previousTrigger;

    private WorkerPumpObservation(DatabaseEngineWorker worker, Action<TimeSpan> delay, Action<TimeSpan> trigger)
    {
        _worker = worker;
        _delay = delay;
        _trigger = trigger;
        _previousDelay = Interlocked.Exchange(ref DelayRequested(worker), delay);
        _previousTrigger = Interlocked.Exchange(ref TriggerWaitCompleted(worker), trigger);
    }

    internal static WorkerPumpObservation Create(DatabaseEngineWorker worker, Action<TimeSpan> delay, Action<TimeSpan> trigger)
        => new(worker, delay, trigger);

    internal Exception? Failure => Volatile.Read(ref ObserverFailure(_worker));

    public void Dispose()
    {
        Interlocked.CompareExchange(ref DelayRequested(_worker), _previousDelay, _delay);
        Interlocked.CompareExchange(ref TriggerWaitCompleted(_worker), _previousTrigger, _trigger);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "DelayRequested")]
    private static extern ref Action<TimeSpan>? DelayRequested(DatabaseEngineWorker worker);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "TriggerWaitCompleted")]
    private static extern ref Action<TimeSpan>? TriggerWaitCompleted(DatabaseEngineWorker worker);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ObserverFailure")]
    private static extern ref Exception? ObserverFailure(DatabaseEngineWorker worker);
}
