using System;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[System.Diagnostics.DebuggerDisplay("GraphTypedAggregateFunction {Name}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphTypedAggregateFunction<,,>.DebugView))]
internal sealed class GraphTypedAggregateFunction<TState, T1, TResult> : GraphAggregateFunction
{
    private readonly Func<TState> _seed;
    private readonly Func<TState, T1, TState> _step;
    private readonly Func<TState, TResult> _finish;
    private readonly Func<TState, long> _retainedBytes;
    internal GraphTypedAggregateFunction(GraphFunctionName name, Func<TState> seed, Func<TState, T1, TState> step,
        Func<TState, TResult> finish, Func<TState, long> retainedBytes, GraphNullBehavior nullBehavior)
        : base(name, [GraphFunctionType.Parameter<T1>(nullBehavior)], GraphFunctionType.Of<TResult>(), nullBehavior)
    {
        ArgumentNullException.ThrowIfNull(seed); ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(finish); ArgumentNullException.ThrowIfNull(retainedBytes);
        _seed = seed; _step = step; _finish = finish; _retainedBytes = retainedBytes;
    }
    protected override GraphAggregateAccumulator CreateAccumulatorCore(scoped in GraphFunctionContext context)
        => new Accumulator(_seed(), _step, _finish, _retainedBytes);
    [System.Diagnostics.DebuggerDisplay("Accumulator")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphTypedAggregateFunction<,,>.Accumulator.DebugView))]
    private sealed class Accumulator : GraphAggregateAccumulator
    {
        private TState _state;
        private readonly Func<TState, T1, TState> _step;
        private readonly Func<TState, TResult> _finish;
        private readonly Func<TState, long> _retainedBytes;
        internal Accumulator(TState state, Func<TState, T1, TState> step, Func<TState, TResult> finish, Func<TState, long> retainedBytes)
        { _state = state; _step = step; _finish = finish; _retainedBytes = retainedBytes; }
        protected override void AddCore(scoped in GraphArguments arguments) => _state = _step(_state, arguments.GetParameter<T1>(0));
        protected override object? FinishCore() => _finish(_state);
        protected override long RetainedBytesCore => _retainedBytes(_state);
        protected override void DisposeCore() { _state = default!; }
        private sealed class DebugView { public DebugView(Accumulator value) { _ = value; } }
    }
    private sealed class DebugView
    {
        private readonly GraphTypedAggregateFunction<TState, T1, TResult> _target;
        public DebugView(GraphTypedAggregateFunction<TState, T1, TResult> value) { _target = value; }
        public GraphFunctionName Name => _target.Name;
    }
}
