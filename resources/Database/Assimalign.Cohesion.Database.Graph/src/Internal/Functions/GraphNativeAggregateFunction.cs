using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[System.Diagnostics.DebuggerDisplay("GraphNativeAggregateFunction {Name}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphNativeAggregateFunction.DebugView))]
internal sealed class GraphNativeAggregateFunction : GraphAggregateFunction
{
    internal GqlAggregateFunction Identity { get; }
    internal GraphNativeAggregateFunction(GqlAggregateFunction identity)
        : base(identity == GqlAggregateFunction.CollectList ? "collect_list" : identity.ToString(),
            [GraphFunctionType.Any], identity == GqlAggregateFunction.Count ? GraphFunctionType.Int64.NotNull() : identity == GqlAggregateFunction.CollectList ? GraphFunctionType.List(GraphFunctionType.Any) : identity is GqlAggregateFunction.Sum or GqlAggregateFunction.Avg ? GraphFunctionType.Number : GraphFunctionType.Any) { Identity = identity; }
    internal override bool IsNative => true;
    internal override bool AcceptsArity(int count) => count == 1 || Identity == GqlAggregateFunction.Count && count == 0;
    protected override GraphAggregateAccumulator CreateAccumulatorCore(scoped in GraphFunctionContext context) => new GraphNativeAggregateAccumulator(Identity);
    private sealed class DebugView
    {
        private readonly GraphNativeAggregateFunction _target;
        public DebugView(GraphNativeAggregateFunction value) { _target = value; }
        public GraphFunctionName Name => _target.Name;
    }
}
