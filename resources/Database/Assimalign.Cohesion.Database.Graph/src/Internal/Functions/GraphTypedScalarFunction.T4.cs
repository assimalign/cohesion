using System;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[System.Diagnostics.DebuggerDisplay("GraphTypedScalarFunction {Name}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphTypedScalarFunction<,,,>.DebugView))]
internal sealed class GraphTypedScalarFunction<T1, T2, T3, TResult> : GraphScalarFunction
{
    private readonly Func<T1, T2, T3, TResult> _body;
    internal GraphTypedScalarFunction(GraphFunctionName name, Func<T1, T2, T3, TResult> body, GraphNullBehavior nullBehavior)
        : base(name, [GraphFunctionType.Parameter<T1>(nullBehavior), GraphFunctionType.Parameter<T2>(nullBehavior), GraphFunctionType.Parameter<T3>(nullBehavior)], GraphFunctionType.Of<TResult>(), nullBehavior)
    { ArgumentNullException.ThrowIfNull(body); _body = body; }
    protected override object? InvokeCore(scoped in GraphArguments arguments) => _body(arguments.Get<T1>(0), arguments.Get<T2>(1), arguments.Get<T3>(2));
    private sealed class DebugView
    {
        private readonly GraphTypedScalarFunction<T1, T2, T3, TResult> _target;
        public DebugView(GraphTypedScalarFunction<T1, T2, T3, TResult> value) { _target = value; }
        public GraphFunctionName Name => _target.Name;
        public GraphFunctionType ReturnType => _target.ReturnType;
    }
}
