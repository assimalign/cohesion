namespace Assimalign.Cohesion.Database.Transactions.Internal;

// Opaque identity: equal ledger positions are still distinct nested scopes. The store's
// live marker list supplies ownership and lifetime; the position alone is never a token.
[System.Diagnostics.DebuggerDisplay("TransactionUndoMarker")]
[System.Diagnostics.DebuggerTypeProxy(typeof(TransactionUndoMarker.DebugView))]
internal sealed class TransactionUndoMarker
{
    internal TransactionUndoMarker(int position)
    {
        Position = position;
    }

    internal int Position { get; }
    internal int LockPosition { get; set; }

    private sealed class DebugView
    {
        public DebugView(TransactionUndoMarker marker) { _ = marker; }
    }
}
