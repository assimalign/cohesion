using System;
using System.Runtime.ExceptionServices;
using System.Threading;

// Deviates from namespace-matches-assembly: model-local linked source.
namespace Assimalign.Cohesion.Database;

// A database may have two file sets. Try both and settle every failure in the collecting pass.
internal abstract class DatabaseWriteWorker<TDatabase> : DatabaseMaintenanceWorker<TDatabase, DatabaseWriteResult>
    where TDatabase : class
{
    protected DatabaseWriteWorker(string name, DatabaseEngineWorkerKind kind, TimeSpan interval)
        : base(name, kind, interval) { }

    protected abstract DatabaseWriteResult RunDatabase(TDatabase database, CancellationToken cancellationToken);
    protected sealed override Func<DatabaseWriteResult> CreateWork(TDatabase database, CancellationToken cancellationToken)
        => () => RunDatabase(database, cancellationToken);

    protected static void TryWrite(Func<int> write, ref DatabaseWriteResult result)
    {
        try { result.Units += write(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            (result.Failures ??= []).Add(exception);
        }
    }

    protected sealed override void SettleDatabase(TDatabase database,
        DatabaseWorkerLanes<DatabaseWriteResult>.Outcome outcome, CancellationToken cancellationToken,
        DatabaseWorkerPass.Visit? visit)
    {
        if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Completed; visit.WorkUnits += outcome.Result.Units; }
        if (outcome.Failure is { } failure) { SettleFailure(database, failure, outcome.CancellationRequested, cancellationToken, visit); }
        if (outcome.Result.Failures is { } failures)
        {
            foreach (var exception in failures) { SettleFailure(database, exception, outcome.CancellationRequested, cancellationToken, visit); }
        }
        if (visit is not null) { visit.Offline = IsOffline(database); }
    }

    private void SettleFailure(TDatabase database, Exception exception, bool originCancelled, CancellationToken cancellationToken, DatabaseWorkerPass.Visit? visit)
    {
        if (exception is ObjectDisposedException && !IsOpen(database))
        {
            if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Closed; }
            return;
        }
        if (exception is OperationCanceledException && (originCancelled || cancellationToken.IsCancellationRequested))
        {
            if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Cancelled; }
            ReportUnfinished(GetName(database));
            if (cancellationToken.IsCancellationRequested) { ExceptionDispatchInfo.Throw(exception); }
            return;
        }
        if (!IsOffline(database))
        {
            ReportFailure(GetName(database), exception);
            if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Failed; visit.Backoff = FailureBackoff; }
        }
    }
}
