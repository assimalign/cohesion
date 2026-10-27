using System;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[System.Diagnostics.DebuggerDisplay("GraphNativeScalarFunction {Name}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphNativeScalarFunction.DebugView))]
internal sealed class GraphNativeScalarFunction : GraphScalarFunction
{
    internal GqlScalarFunction Identity { get; }
    internal GraphNativeScalarFunction(GqlScalarFunction identity)
        : base(GraphScalarFunctions.Name(identity), Enumerable.Range(0, Maximum(identity)).Select(index => GraphFunctionType.Native(GraphScalarFunctions.Argument(identity, index))), Result(identity))
    { Identity = identity; }
    private static GraphFunctionType Result(GqlScalarFunction identity) => identity switch
    {
        GqlScalarFunction.Nodes => GraphFunctionType.List(GraphFunctionType.Node.NotNull()),
        GqlScalarFunction.Relationships => GraphFunctionType.List(GraphFunctionType.Relationship.NotNull()),
        GqlScalarFunction.Labels or GqlScalarFunction.Keys => GraphFunctionType.List(GraphFunctionType.String.NotNull()),
        GqlScalarFunction.Properties => GraphFunctionType.Map(GraphFunctionType.Any),
        _ => GraphFunctionType.Native(GraphScalarFunctions.Result(identity), GraphScalarFunctions.Type(identity)),
    };
    private static int Maximum(GqlScalarFunction identity) => identity switch
    {
        GqlScalarFunction.CurrentDate or GqlScalarFunction.CurrentTimestamp => 0,
        GqlScalarFunction.Substring or GqlScalarFunction.Replace => 3,
        GqlScalarFunction.Mod or GqlScalarFunction.Round or GqlScalarFunction.Log => 2, _ => 1,
    };
    internal override bool IsNative => true;
    internal override bool AcceptsArity(int count) => Identity switch
    {
        GqlScalarFunction.Substring => count is 2 or 3,
        GqlScalarFunction.Round or GqlScalarFunction.Log => count is 1 or 2, _ => count == Parameters.Count,
    };
    protected override object? InvokeCore(scoped in GraphArguments arguments)
        => GraphExpressionEvaluator.InvokeBuiltin(Identity, arguments.Values, arguments.Context.Evaluation);
    private sealed class DebugView
    {
        private readonly GraphNativeScalarFunction _target;
        public DebugView(GraphNativeScalarFunction value) { _target = value; }
        public GraphFunctionName Name => _target.Name;
    }
}
