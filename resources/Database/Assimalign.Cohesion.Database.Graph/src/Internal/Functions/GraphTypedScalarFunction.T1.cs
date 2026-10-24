using System;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[System.Diagnostics.DebuggerDisplay("GraphTypedScalarFunction {Name}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphTypedScalarFunction<>.DebugView))]
internal sealed class GraphTypedScalarFunction<TResult> : GraphScalarFunction
{
    private readonly Func<TResult> _body;
    internal GraphTypedScalarFunction(GraphFunctionName name, Func<TResult> body)
        : base(name, [], GraphFunctionType.Of<TResult>())
    { ArgumentNullException.ThrowIfNull(body); _body = body; }
    protected override object? InvokeCore(scoped in GraphArguments arguments) => _body();
    private sealed class DebugView
    {
        private readonly GraphTypedScalarFunction<TResult> _target;
        public DebugView(GraphTypedScalarFunction<TResult> value) { _target = value; }
        public GraphFunctionName Name => _target.Name;
        public GraphFunctionType ReturnType => _target.ReturnType;
    }
}
