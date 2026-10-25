using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

// Deviates from namespace-matches-assembly: model-local linked source.
namespace Assimalign.Cohesion.Database;

// #1226: a refused undo retains writer locks, so its coordinator owns a short doubling
// retry schedule independent of the maintenance interval. Owner decision 42 keeps a failed
// full pass recorded until a full pass succeeds; an undo-only visit cannot settle that work.
// Owner decision 46 retries a failed full pass after FailureBackoff for that database alone,
// on the engine clock, so give-up timing is independent of the full maintenance interval.
// Retry/refusal state belongs exclusively to the pump. A lane returns facts about the work it
// attempted; settlement updates the state only after Collect transferred ownership back.
internal abstract class DatabaseVersionPurgeWorker<TDatabase> : DatabaseMaintenanceWorker<TDatabase, DatabasePurgeResult>
    where TDatabase : class
{
    private readonly TimeProvider _clock;
    private long _lastFullPass;
    private long _epoch;
    private readonly Dictionary<TDatabase, State> _states = new(ReferenceEqualityComparer.Instance);
    private long _nextFullPassRetry = long.MaxValue;
    private const int busyPassLimit = 3;

    protected DatabaseVersionPurgeWorker(string engineName, TimeSpan interval, TimeProvider clock)
        : base(engineName + "/version-purge", DatabaseEngineWorkerKind.VersionPurge, interval)
    {
        _clock = clock;
        _lastFullPass = clock.GetTimestamp();
    }

    internal Action<TDatabase>? BeforeFullPass { get; set; }
    internal Action<TDatabase>? BeforeDeferredUndoRetry { get; set; }
    protected abstract TransactionCoordinator GetCoordinator(TDatabase database);
    protected abstract void Purge(TDatabase database, CancellationToken cancellationToken);
    protected virtual bool HasReclamation(TDatabase database) => false;
    protected virtual void RequestReclamation(TDatabase database) { }
    protected virtual void Reclaim(TDatabase database, CancellationToken cancellationToken) { }
    protected virtual bool ReclamationReady => false;
    protected virtual bool HasReclamationSupport => false;

    protected override TimeSpan TriggerTimeout
    {
        get
        {
            // At the cap, retry only when a runner completes (its signal) or the backstop
            // passes. An overdue undo/full retry must not spin while it cannot get a lane.
            if (Lanes.IsSaturated) { return Interval < FailureBackoff ? Interval : FailureBackoff; }
            var wait = Interval - _clock.GetElapsedTime(Volatile.Read(ref _lastFullPass));
            long retry = Volatile.Read(ref _nextFullPassRetry);
            if (retry != long.MaxValue)
            {
                var until = _clock.GetElapsedTime(_clock.GetTimestamp(), retry);
                if (until < wait) { wait = until; }
            }
            foreach (var database in GetDatabases())
            {
                if (IsClosed(database) || IsOffline(database) || Lanes.IsRunning(database)) { continue; }
                if (ReclamationReady && HasReclamation(database) && wait > TimeSpan.FromMilliseconds(25))
                {
                    wait = TimeSpan.FromMilliseconds(25);
                }
                if (GetCoordinator(database).NextDeferredUndoRetry is { } undo && undo < wait) { wait = undo; }
            }
            return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromMilliseconds(int.MaxValue)
                ? TimeSpan.FromMilliseconds(int.MaxValue) : wait;
        }
    }

    protected sealed override void PreparePass(TDatabase[] databases)
    {
        List<TDatabase>? gone = null;
        foreach (var database in _states.Keys)
        {
            if (!IsOpen(database) || IsOffline(database)) { (gone ??= []).Add(database); }
        }
        if (gone is not null) { foreach (var database in gone) { _states.Remove(database); } }
        long now = _clock.GetTimestamp();
        bool full = _clock.GetElapsedTime(Volatile.Read(ref _lastFullPass), now) >= Interval;
        if (full)
        {
            Volatile.Write(ref _lastFullPass, now);
            _epoch++;
        }
        foreach (var database in databases)
        {
            if (!_states.TryGetValue(database, out var state)) { _states.Add(database, state = new State()); }
            // A full pass that becomes due while its prior lane runs stays owed after collection.
            if (full) { state.OwedEpoch = _epoch; }
        }
    }

    protected sealed override bool IsUnfinished(TDatabase database)
        => _states[database].RetryAt.HasValue || _states[database].Refusal is not null
            || GetCoordinator(database).NextDeferredUndoRetry is not null;

    protected sealed override bool IsDue(TDatabase database)
    {
        var state = _states[database];
        return BeforeDeferredUndoRetry is not null || state.OwedEpoch > state.AttemptedEpoch
            || (state.RetryAt is { } retry && _clock.GetTimestamp() >= retry)
            || GetCoordinator(database).NextDeferredUndoRetry is { } undo && undo <= TimeSpan.Zero
            || ReclamationReady && HasReclamation(database);
    }

    protected sealed override Func<DatabasePurgeResult> CreateWork(TDatabase database, CancellationToken cancellationToken)
    {
        var state = _states[database];
        bool full = state.OwedEpoch > state.AttemptedEpoch || (state.RetryAt is { } retry && _clock.GetTimestamp() >= retry);
        state.InFlightFull = full;
        state.InFlightEpoch = state.OwedEpoch;
        bool previousFullFailed = state.RetryAt.HasValue;
        return () => Execute(database, full, previousFullFailed, cancellationToken);
    }

    private DatabasePurgeResult Execute(TDatabase database, bool full, bool previousFullFailed, CancellationToken cancellationToken)
    {
        var result = new DatabasePurgeResult();
        try
        {
            if (full)
            {
                BeforeFullPass?.Invoke(database);
                RequestReclamation(database);
                Purge(database, cancellationToken);
                result.FullCompleted = true;
            }
            else
            {
                BeforeDeferredUndoRetry?.Invoke(database);
                result.UndoRetried = true;
                GetCoordinator(database).RetryDeferredUndo(cancellationToken);
            }
            if (HasReclamation(database) && (full || !previousFullFailed)) { Reclaim(database, cancellationToken); }
            result.UndoPending = GetCoordinator(database).NextDeferredUndoRetry is not null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result.Failure = exception;
            if (!IsClosed(database) && !IsOffline(database) && IsOpen(database))
            {
                result.ReclamationPending = HasReclamation(database);
                if (exception is StorageTransactionException { Code: not StorageTransactionErrorCode.ForeignStorage })
                {
                    result.Writer = GetCoordinator(database).VersionStore.PendingAbortedPurges.DefaultIfEmpty().Min();
                }
            }
        }
        return result;
    }

    protected sealed override void SettleDatabase(TDatabase database, DatabaseWorkerLanes<DatabasePurgeResult>.Outcome outcome,
        CancellationToken cancellationToken, DatabaseWorkerPass.Visit? visit)
    {
        var state = _states[database];
        var result = outcome.Result;
        var failure = outcome.Failure ?? result.Failure;
        bool full = state.InFlightFull;
        if (visit is not null)
        {
            visit.WorkUnits = result.FullCompleted ? 1 : 0;
            visit.UndoRetried = result.UndoRetried;
            visit.UndoPending = result.UndoPending;
        }
        bool fullPending = full && !result.FullCompleted;
        if (full) { state.AttemptedEpoch = state.InFlightEpoch; }
        if (result.FullCompleted)
        {
            state.RetryAt = null;
            state.FullPassPending = false;
        }
        if (failure is OperationCanceledException && (outcome.CancellationRequested || cancellationToken.IsCancellationRequested))
        {
            if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Cancelled; }
            ReportUnfinished(GetName(database));
            if (cancellationToken.IsCancellationRequested) { ExceptionDispatchInfo.Throw(failure); }
            return;
        }
        if (IsClosed(database) || IsOffline(database) || !IsOpen(database))
        {
            _states.Remove(database);
            if (visit is not null)
            {
                visit.Offline = IsOffline(database);
                visit.Reason = visit.Offline ? DatabaseWorkerPass.VisitReason.Offline : DatabaseWorkerPass.VisitReason.Closed;
            }
            return;
        }
        if (failure is StorageTransactionException { Code: not StorageTransactionErrorCode.ForeignStorage } busy)
        {
            if (fullPending) { ScheduleRetry(state); }
            ReportBusy(database, state, busy, result.Writer, fullPending);
            ReportUnfinished(GetName(database));
            if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Busy; }
            return;
        }
        if (failure is not null)
        {
            ClearBusy(state);
            ReportFailure(GetName(database), failure, TimeSpan.Zero);
            if (full || result.ReclamationPending) { ScheduleRetry(state); }
            if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Failed; }
            return;
        }
        if (full || (!result.UndoPending && (state.Writer != 0 || (HasReclamationSupport && !state.FullPassPending)))) { ClearBusy(state); }
        bool unfinished = result.UndoPending || state.RetryAt.HasValue || state.Refusal is not null;
        if (unfinished) { ReportUnfinished(GetName(database)); }
        if (visit is not null) { visit.Reason = unfinished ? DatabaseWorkerPass.VisitReason.Deferred : DatabaseWorkerPass.VisitReason.Completed; }
    }

    protected sealed override void FinishPass(CancellationToken cancellationToken, DatabaseWorkerPass? pass)
    {
        long next = long.MaxValue;
        foreach (var (database, state) in _states)
        {
            if (IsClosed(database) || IsOffline(database) || !IsOpen(database) || Lanes.IsRunning(database)) { continue; }
            if (state.OwedEpoch > state.AttemptedEpoch) { next = _clock.GetTimestamp(); }
            else if (state.RetryAt is { } retry && retry < next) { next = retry; }
        }
        Volatile.Write(ref _nextFullPassRetry, next);
    }

    private void ReportBusy(TDatabase database, State state, StorageTransactionException exception, ulong writer, bool fullPending)
    {
        // The refused page/bracket identifies work, not unrelated WAL activity (#1277).
        // PostgreSQL bounds the conflicting-lock retry in src/backend/access/heap/vacuumlazy.c:3205-3219.
        bool same = state.Refusal == exception.Message;
        int passes = same ? state.Passes + 1 : 1;
        long first = same ? state.FirstRefusedAt : _clock.GetTimestamp();
        bool reported = same && state.Reported;
        if (!reported && passes >= busyPassLimit && _clock.GetElapsedTime(first) >= FailureBackoff)
        {
            ReportFailure(GetName(database), exception, TimeSpan.Zero, countsTowardOffline: false);
            reported = true;
        }
        state.Writer = writer;
        state.Refusal = exception.Message;
        state.Passes = Math.Min(passes, busyPassLimit);
        state.FirstRefusedAt = first;
        state.Reported = reported;
        state.FullPassPending |= fullPending;
    }

    private void ScheduleRetry(State state)
        => state.RetryAt = _clock.GetTimestamp() + (long)(FailureBackoff.TotalSeconds * _clock.TimestampFrequency);

    private static void ClearBusy(State state)
    {
        state.Writer = 0;
        state.Refusal = null;
        state.Passes = 0;
        state.FirstRefusedAt = 0;
        state.Reported = false;
        state.FullPassPending = false;
    }

    private sealed class State
    {
        internal long OwedEpoch;
        internal long AttemptedEpoch;
        internal long InFlightEpoch;
        internal bool InFlightFull;
        internal long? RetryAt;
        internal ulong Writer;
        internal string? Refusal;
        internal long FirstRefusedAt;
        internal int Passes;
        internal bool Reported;
        internal bool FullPassPending;
    }
}
