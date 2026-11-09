using System;
using System.Diagnostics;
using System.Threading;

// Deviates from the repo namespace-matches-assembly rule: model-local Indexing build
// infrastructure is compiled into models; no instance crosses an assembly boundary.
namespace Assimalign.Cohesion.Database.Indexing.Internal;

[DebuggerDisplay("IndexBuildBudget")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class IndexBuildBudget
{
    private readonly long _limit;
    private readonly CancellationToken _token;
    private readonly Func<long, Exception> _refusal;
    private long _bytes;

    internal IndexBuildBudget(long limit, CancellationToken token, Func<long, Exception> refusal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        _limit = limit;
        _token = token;
        _refusal = refusal;
    }

    internal void Charge(long bytes)
    {
        _token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes > _limit - _bytes) { throw _refusal(_limit); }
        _bytes += bytes;
    }

    internal Reservation Reserve() => new(this);

    [DebuggerDisplay("Reservation")]
    [DebuggerTypeProxy(typeof(Reservation.DebugView))]
    internal sealed class Reservation : IDisposable
    {
        private readonly IndexBuildBudget _owner;
        private long _bytes;
        internal Reservation(IndexBuildBudget owner) { _owner = owner; }
        internal long Bytes => _bytes;
        internal void Charge(long bytes) { _owner.Charge(bytes); _bytes += bytes; }
        public void Dispose() { _owner._bytes -= _bytes; _bytes = 0; }
        private sealed class DebugView
        {
            private readonly Reservation _target;
            public DebugView(Reservation target) { _target = target; }
            public long Bytes => _target._bytes;
        }
    }

    private sealed class DebugView
    {
        private readonly IndexBuildBudget _target;
        public DebugView(IndexBuildBudget target) { _target = target; }
        public long Limit => _target._limit;
        public long Bytes => _target._bytes;
    }
}
