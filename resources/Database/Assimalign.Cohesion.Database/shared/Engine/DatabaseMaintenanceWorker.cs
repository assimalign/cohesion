using System;
using System.Collections.Generic;
using System.Threading;

// Deviates from namespace-matches-assembly: model-local linked source.
namespace Assimalign.Cohesion.Database;

// The pump owns scheduling and settlement; one lane owns a database's storage work until collected.
internal abstract class DatabaseMaintenanceWorker<TDatabase, TResult> : DatabaseLaneWorker<TResult>
    where TDatabase : class
{
    private TDatabase? _lastServed;
    private readonly HashSet<TDatabase> _capacityDeferred = new(ReferenceEqualityComparer.Instance);

    protected DatabaseMaintenanceWorker(string name, DatabaseEngineWorkerKind kind, TimeSpan interval)
        : base(name, kind, interval) { }

    protected abstract TDatabase[] GetDatabases();
    protected abstract string GetName(TDatabase database);
    protected abstract bool IsClosed(TDatabase database);
    protected abstract bool IsOffline(TDatabase database);
    protected abstract bool IsOpen(TDatabase database);
    protected abstract Func<TResult> CreateWork(TDatabase database, CancellationToken cancellationToken);
    protected abstract void SettleDatabase(TDatabase database, DatabaseWorkerLanes<TResult>.Outcome outcome,
        CancellationToken cancellationToken, DatabaseWorkerPass.Visit? visit);
    protected virtual bool RepeatAfterCollection => false;
    protected abstract bool IsDue(TDatabase database);
    protected virtual bool IsUnfinished(TDatabase database) => false;
    protected virtual bool HasConcurrentWork(TDatabase database) => false;
    protected virtual object GetLaneKey(TDatabase database) => database;
    protected virtual void PreparePass(TDatabase[] databases) { }
    protected virtual void FinishPass(CancellationToken cancellationToken, DatabaseWorkerPass? pass) { }

    // On the dispatching type, so tests can hold a purge or index lane without widening a boundary.
    internal Action<TDatabase>? BeforeDatabase { get; set; }
    internal Action<TDatabase, TResult>? AfterDatabase { get; set; }

    protected sealed override void RunLanePass(CancellationToken cancellationToken, DatabaseWorkerPass? pass)
    {
        var databases = GetDatabases();
        List<TDatabase>? gone = null;
        foreach (var database in _capacityDeferred)
        {
            if (IsClosed(database) || IsOffline(database) || !IsOpen(database)) { (gone ??= []).Add(database); }
        }
        if (gone is not null) { foreach (var database in gone) { _capacityDeferred.Remove(database); } }
        PreparePass(databases);
        try
        {
            int start = databases.Length == 0 ? 0 : (Array.IndexOf(databases, _lastServed) + 1) % databases.Length;
            for (int offset = 0; offset < databases.Length; offset++)
            {
                if (cancellationToken.IsCancellationRequested) { break; }
                var database = databases[(start + offset) % databases.Length];
                Visit(database, cancellationToken, pass?.Add(GetName(database)));
            }
        }
        finally { FinishPass(cancellationToken, pass); }
    }

    private void Visit(TDatabase database, CancellationToken cancellationToken, DatabaseWorkerPass.Visit? visit)
    {
        string name = GetName(database);
        var state = Lanes.Collect(GetLaneKey(database), out var outcome);
        if (state == DatabaseWorkerLanes<TResult>.LaneState.Running)
        {
            if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Running; visit.Offline = IsOffline(database); }
            if (!IsClosed(database) && !IsOffline(database)) { ReportUnfinished(name); }
            return;
        }
        if (IsClosed(database) || IsOffline(database))
        {
            if (visit is not null)
            {
                visit.Offline = IsOffline(database);
                visit.Reason = visit.Offline ? DatabaseWorkerPass.VisitReason.Offline : DatabaseWorkerPass.VisitReason.Closed;
            }
            return;
        }

        if (HasConcurrentWork(database))
        {
            if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Running; }
            ReportUnfinished(name);
            return;
        }
        bool begun = BeginDatabase(name);
        if (state == DatabaseWorkerLanes<TResult>.LaneState.Ended)
        {
            if (visit is not null) { visit.Collected = true; }
            SettleDatabase(database, outcome, cancellationToken, visit);
            // A grouped commit can register after the detached flush covered its waiters but
            // before this pass reset the signal. Recheck it now, before sleeping for the window.
            if (!RepeatAfterCollection || IsClosed(database) || IsOffline(database) || !BeginDatabase(name)) { return; }
            begun = true;
        }
        if (!begun)
        {
            if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.BackingOff; visit.Backoff = FailureBackoff; }
            return;
        }

        bool workDue = IsDue(database) || BeforeDatabase is not null;
        if (!workDue) { _capacityDeferred.Remove(database); }
        bool due = workDue && !(CompletionOnly && state == DatabaseWorkerLanes<TResult>.LaneState.Idle
            && !_capacityDeferred.Contains(database));
        if (visit is not null) { visit.WorkDue = due; }
        if (!due)
        {
            if (IsUnfinished(database)) { ReportUnfinished(name); }
            if (visit is not null && !visit.Collected) { visit.Reason = DatabaseWorkerPass.VisitReason.NotDue; }
            return;
        }
        if (visit is not null) { visit.Attempted = true; }
        try
        {
            if (!Dispatch(database, cancellationToken, out outcome, visit))
            {
                if (visit is not null && visit.Reason != DatabaseWorkerPass.VisitReason.Capacity) { visit.Reason = DatabaseWorkerPass.VisitReason.Running; }
                if (!Lanes.HasLane(GetLaneKey(database)))
                {
                    _capacityDeferred.Add(database);
                    ReportCapacity(name);
                }
                ReportUnfinished(name);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Cancelled; }
            ReportUnfinished(name);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            outcome = new(default!, exception);
        }
        SettleDatabase(database, outcome, cancellationToken, visit);
    }

    private bool Dispatch(TDatabase database, CancellationToken cancellationToken,
        out DatabaseWorkerLanes<TResult>.Outcome outcome, DatabaseWorkerPass.Visit? visit)
    {
        var work = CreateWork(database, cancellationToken);
        bool ended = Lanes.Run(GetLaneKey(database), () =>
        {
            BeforeDatabase?.Invoke(database);
            var result = work();
            AfterDatabase?.Invoke(database, result);
            return result;
        }, LaneSignal, LaneTimeout, cancellationToken, out outcome, visit, GetName(database));
        if (ended || Lanes.HasLane(GetLaneKey(database)))
        {
            _lastServed = database;
            _capacityDeferred.Remove(database);
        }
        return ended;
    }
}
