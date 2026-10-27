using System;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentTypedAggregateFunction {Name}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentTypedAggregateFunction<,,>.DebugView))]
internal sealed class DocumentTypedAggregateFunction<TState, T1, TResult> : DocumentAggregateFunction
{
    private readonly Func<TState> _seed;
    private readonly Func<TState, T1, TState> _step;
    private readonly Func<TState, TResult> _finish;
    internal DocumentTypedAggregateFunction(DocumentFunctionName name, Func<TState> seed, Func<TState, T1, TState> step,
        Func<TState, TResult> finish, DocumentNullBehavior nullBehavior)
        : base(name, 1, DocumentTypedValue<TResult>.Type, nullBehavior, DocumentTypedValue<T1>.Kind)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(finish);
        DocumentTypedValue<T1>.RequireNullable(nullBehavior);
        _seed = seed; _step = step; _finish = finish;
    }
    protected override DocumentAggregateAccumulator CreateAccumulatorCore(scoped in DocumentFunctionContext context) => new Accumulator(this, _seed());

    [global::System.Diagnostics.DebuggerDisplay("Accumulator")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentTypedAggregateFunction<,,>.Accumulator.DebugView))]
    private sealed class Accumulator : DocumentAggregateAccumulator
    {
        private readonly DocumentTypedAggregateFunction<TState, T1, TResult> _function;
        private TState _state;
        private bool _seen;
        internal Accumulator(DocumentTypedAggregateFunction<TState, T1, TResult> function, TState state) { _function = function; _state = state; }
        protected override void AddCore(scoped in DocumentArguments arguments) { _state = _function._step(_state, DocumentTypedValue<T1>.Read(arguments[0])); _seen = true; }
        protected override DocumentValue FinishCore() => _seen ? DocumentTypedValue<TResult>.Write(_function._finish(_state)) : DocumentValue.Null;

        private sealed class DebugView
        {
            public DebugView(Accumulator value) { _ = value; }
        }
    }
    private sealed class DebugView
    {
        private readonly DocumentTypedAggregateFunction<TState, T1, TResult> _target;
        public DebugView(DocumentTypedAggregateFunction<TState, T1, TResult> value) { _target = value; }
        public DocumentFunctionName Name => _target.Name;
    }
}
