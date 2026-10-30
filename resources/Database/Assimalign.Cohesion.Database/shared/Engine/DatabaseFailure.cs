using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;

// Deviates from the namespace-matches-assembly rule: stateless shared source retains its owning namespace.
namespace Assimalign.Cohesion.Database.Internal;

// Stateless exception inspection linked into the model engines. Successful statements never
// call this walker; unwrapped failures avoid its traversal allocations too.
internal static class DatabaseFailure
{
    internal static T? Find<T>(Exception? error) where T : Exception
    {
        if (error is T found) { return found; }
        if (error is null || (error.InnerException is null && error is not AggregateException)) { return null; }
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        while (pending.TryPop(out var next))
        {
            if (!visited.Add(next)) { continue; }
            if (next is T match) { return match; }
            if (next is AggregateException aggregate)
            {
                for (int index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                { pending.Push(aggregate.InnerExceptions[index]); }
            }
            else if (next.InnerException is { } inner) { pending.Push(inner); }
        }
        return null;
    }

    internal static DatabaseException? TranslateUndoFailure(Exception error)
        => error is DatabaseException { Diagnostics: [{ Code: "CK04009" }, ..] } pending ? pending
            : Transactions.TransactionCoordinator.GetUndoFailureCode(error) is { } code
            ? new DatabaseException(error.Message, [new Types.Diagnostic
            { Code = code, Message = error.Message, Severity = Types.DiagnosticSeverity.Error }], error)
            : null;

    // Named controls first use the model's ordinary statement translator. Only then may a
    // still-active undo fence replace its code, retaining that translated cause. Cancellation
    // before admission and lifecycle/corruption failures keep their existing contracts.
    internal static Exception TranslateSavepointFailure(Exception reported, Transactions.TransactionContext? context)
    {
        bool admittedCancellation = false;
        if (Find<OperationCanceledException>(reported) is { } cancellation)
        {
            // No root/kernel lock is held or acquired under exception-data sync. The root's
            // owned carrier distinguishes a fault in undo from cancellation before admission.
            var data = cancellation.Data;
            lock (data.SyncRoot)
            {
                admittedCancellation = data[typeof(Transactions.TransactionSavepoint)] is true;
                data.Remove(typeof(Transactions.TransactionSavepoint));
            }
        }
        return context is not null && Transactions.TransactionCoordinator.IsPartialUndoPending(context)
            && (reported is not OperationCanceledException || admittedCancellation)
            && Find<DatabaseOfflineException>(reported) is null
            && Find<DatabaseTransactionCommitUnconfirmedException>(reported) is null
            && Find<StorageOfflineException>(reported) is null
            && reported is not DatabaseException { Diagnostics: [{ Code: "CK07004" }, ..] }
            && Find<ObjectDisposedException>(reported) is null
            && !IsCorruption(reported)
            ? reported is DatabaseException { Diagnostics: [{ Code: "CK04009" }, ..] } ? reported
                : new DatabaseException(reported.Message, [new Types.Diagnostic
                { Code = "CK04009", Message = reported.Message, Severity = Types.DiagnosticSeverity.Error }], reported)
            : reported;
    }

    internal static bool IsCorruption(Exception error)
        => Find<DatabaseCorruptionException>(error) is not null
            || StorageCorruptionException.Find(error) is not null || Find<IndexCorruptionException>(error) is not null;

    internal static bool IsStorageFailure(Exception error)
        => IsCorruption(error) || (error is not DatabaseException { Diagnostics: [{ Code: "CK04009" }, ..] }
            && Transactions.TransactionCoordinator.GetUndoFailureCode(error) != "CK04009"
            && Find<StorageOfflineException>(error) is null
            && (Find<StorageException>(error) is not null || Find<IOException>(error) is not null
                || error is DatabaseCorruptionException
                || (Find<IndexException>(error) is not null
                    && (error is not (DatabaseTransactionAbortedException or DatabaseTransactionDeadlockException
                            or Transactions.TransactionAbortedException or Transactions.TransactionDeadlockException)
                        || Find<IndexCorruptionException>(error) is not null))));

}
