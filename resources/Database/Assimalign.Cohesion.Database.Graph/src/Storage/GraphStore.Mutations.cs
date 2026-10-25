using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage;

public sealed partial class GraphStore
{
    private readonly ConditionalWeakTable<TransactionContext, MutationGate> _mutationGates = new();
    private readonly ConditionalWeakTable<TransactionContext, MutationGate>.CreateValueCallback _mutationGateFactory;

    // Instance-local encoding hook, reachable only by this store's own test assembly.
    internal Func<ValueTask>? OnEncodingAsync { get; set; }

    private async ValueTask<MutationLease> EnterMutationAsync(TransactionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Lock order: transaction mutation gate -> database writer lock -> coordinator
        // apply gate -> B+Tree gate -> storage page latches. Directory _sync is never held
        // across an await. Incident readers take only index/page latches; directory readers
        // may take _sync then page latches, never an index latch or these writer gates.
        var gate = _mutationGates.GetValue(context, _mutationGateFactory);
        await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LockAsync(context, cancellationToken).ConfigureAwait(false);
            return new MutationLease(gate);
        }
        catch { gate.Semaphore.Release(); throw; }
    }

    private MutationGate CreateMutationGate(TransactionContext context)
    {
        // Statement views share the original transaction's gate. Cache the association
        // weakly so completed transactions and their views need no explicit cleanup.
        foreach (var transaction in _coordinator.GetOpenContexts())
        {
            if (transaction.Sequence == context.Sequence && !ReferenceEquals(transaction, context))
            { return _mutationGates.GetValue(transaction, static owner => new MutationGate(owner)); }
        }
        return new MutationGate(context);
    }

    private async ValueTask<byte[]> EncodeAsync(GraphRecord record, TransactionSequence writer)
    {
        if (OnEncodingAsync is { } hook) { await hook().ConfigureAwait(false); }
        return GraphRecordCodec.Encode(record, writer);
    }

    // A value lease and SemaphoreSlim's completed wait keep repeated uncontended
    // entries allocation-free after the transaction's gate has been associated.
    [global::System.Diagnostics.DebuggerDisplay("MutationLease")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Storage.GraphStore.MutationLease.DebugView))]
    private readonly struct MutationLease : IDisposable
    {
        private readonly MutationGate _gate;
        internal MutationLease(MutationGate gate) { _gate = gate; }
        internal TransactionContext RefreshSnapshot(TransactionContext context)
            => context.IsolationLevel == IsolationLevel.ReadCommitted && ReferenceEquals(context, _gate.Transaction)
                ? context.PinStatementSnapshot() : context;
        public void Dispose() => _gate.Semaphore.Release();

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Graph.Storage.GraphStore.MutationLease value)
            {
                _ = value;
            }
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("MutationGate")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Storage.GraphStore.MutationGate.DebugView))]
    private sealed class MutationGate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal readonly TransactionContext Transaction;
        internal MutationGate(TransactionContext transaction) { Transaction = transaction; }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Graph.Storage.GraphStore.MutationGate value)
            {
                _ = value;
            }
        }
    }
}
