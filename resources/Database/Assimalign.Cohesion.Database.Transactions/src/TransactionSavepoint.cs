using System.Diagnostics;

using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>An opaque, memory-only undo scope owned by one coordinator and transaction.</summary>
/// <remarks>Release discards this scope and its descendants. Rollback retains this scope for reuse.
/// Callers serialize operations of the same transaction, including reads and lock requests.</remarks>
[DebuggerDisplay("TransactionSavepoint")]
[DebuggerTypeProxy(typeof(TransactionSavepoint.DebugView))]
public sealed class TransactionSavepoint
{
    internal TransactionSavepoint(TransactionCoordinator coordinator, TransactionContext context, TransactionUndoMarker marker)
    {
        Coordinator = coordinator;
        Context = context;
        Marker = marker;
    }

    internal TransactionCoordinator Coordinator { get; }
    internal TransactionContext Context { get; }
    internal TransactionUndoMarker Marker { get; }

    private sealed class DebugView
    {
        public DebugView(TransactionSavepoint savepoint) { _ = savepoint; }
    }
}
