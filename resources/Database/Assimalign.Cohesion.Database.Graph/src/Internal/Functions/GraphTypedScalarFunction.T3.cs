using System;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[System.Diagnostics.DebuggerDisplay("GraphTypedScalarFunction {Name}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphTypedScalarFunction<,,>.DebugView))]
internal sealed class GraphTypedScalarFunction<T1, T2, TResult> : GraphScalarFunction
{
    private readonly Func<T1, T2, TResult> _body;
    internal GraphTypedScalarFunction(GraphFunctionName name, Func<T1, T2, TResult> body, GraphNullBehavior nullBehavior)
        : base(name, [GraphFunctionType.Of<T1>(), GraphFunctionType.Of<T2>()], GraphFunctionType.Of<TResult>(), nullBehavior)
    { ArgumentNullException.ThrowIfNull(body); _body = body; }
    protected override object? InvokeCore(scoped in GraphArguments arguments) => _body(arguments.Get<T1>(0), arguments.Get<T2>(1));
    private sealed class DebugView
    {
        private readonly GraphTypedScalarFunction<T1, T2, TResult> _target;
        public DebugView(GraphTypedScalarFunction<T1, T2, TResult> value) { _target = value; }
        public GraphFunctionName Name => _target.Name;
        public GraphFunctionType ReturnType => _target.ReturnType;
    }
}
