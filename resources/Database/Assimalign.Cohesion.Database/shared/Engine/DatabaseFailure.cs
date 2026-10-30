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
        => Transactions.TransactionCoordinator.GetUndoFailureCode(error) is { } code
            ? new DatabaseException(error.Message, [new Types.Diagnostic
            { Code = code, Message = error.Message, Severity = Types.DiagnosticSeverity.Error }], error)
            : null;

    internal static bool IsStorageFailure(Exception error)
        => Find<StorageOfflineException>(error) is null
            && (Find<StorageException>(error) is not null || Find<IOException>(error) is not null
                || error is DatabaseCorruptionException
                || (Find<IndexException>(error) is not null
                    && (error is not (DatabaseTransactionAbortedException or DatabaseTransactionDeadlockException
                            or Transactions.TransactionAbortedException or Transactions.TransactionDeadlockException)
                        || Find<IndexCorruptionException>(error) is not null)));

}
