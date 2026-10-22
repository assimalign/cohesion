using System;
using System.Threading;

// Deviates from namespace-matches-assembly: model-local linked source.
namespace Assimalign.Cohesion.Database;

// The pump owns scheduling and settlement; one lane owns a database's storage work until collected.
internal abstract class DatabaseMaintenanceWorker<TDatabase, TResult> : DatabaseLaneWorker<TResult>
    where TDatabase : class
{
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
    protected virtual void PreparePass(TDatabase[] databases) { }
    protected virtual void FinishPass(CancellationToken cancellationToken, DatabaseWorkerPass? pass) { }

    // On the dispatching type, so tests can hold a purge or index lane without widening a boundary.
    internal Action<TDatabase>? BeforeDatabase { get; set; }
    internal Action<TDatabase, TResult>? AfterDatabase { get; set; }

    protected sealed override void RunLanePass(CancellationToken cancellationToken, DatabaseWorkerPass? pass)
    {
        var databases = GetDatabases();
        PreparePass(databases);
        try
        {
            foreach (var database in databases)
            {
                if (cancellationToken.IsCancellationRequested) { break; }
                Visit(database, cancellationToken, pass?.Add(GetName(database)));
            }
        }
        finally { FinishPass(cancellationToken, pass); }
    }

    private void Visit(TDatabase database, CancellationToken cancellationToken, DatabaseWorkerPass.Visit? visit)
    {
        string name = GetName(database);
        var state = Lanes.Collect(database, out var outcome);
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

        if (visit is not null) { visit.Attempted = true; }
        try
        {
            if (!Dispatch(database, cancellationToken, out outcome, visit))
            {
                if (visit is not null && visit.Reason != DatabaseWorkerPass.VisitReason.Capacity) { visit.Reason = DatabaseWorkerPass.VisitReason.Running; }
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
        return Lanes.Run(database, () =>
        {
            BeforeDatabase?.Invoke(database);
            var result = work();
            AfterDatabase?.Invoke(database, result);
            return result;
        }, LaneSignal, LaneTimeout, cancellationToken, out outcome, visit);
    }
}
