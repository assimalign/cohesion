using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[System.Diagnostics.DebuggerDisplay("GraphNativeAggregateAccumulator")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphNativeAggregateAccumulator.DebugView))]
internal sealed class GraphNativeAggregateAccumulator : GraphAggregateAccumulator
{
    private readonly GqlAggregateFunction _identity;
    private GraphBuiltinAggregateState? _state;
    internal GraphNativeAggregateAccumulator(GqlAggregateFunction identity)
    { _identity = identity; }
    internal void Configure(GqlAggregateExpression expression, GraphMemoryOwner? memory) => _state = new(expression, memory);
    protected override void AddCore(scoped in GraphArguments arguments) => State.Add(arguments.Count == 0 ? 1L : arguments[0], arguments.Context.CancellationToken);
    protected override object? FinishCore() => State.Finish();
    private GraphBuiltinAggregateState State => _state ??= new(new GqlAggregateExpression(_identity, null));
    protected override long RetainedBytesCore => 0;
    private sealed class DebugView { public DebugView(GraphNativeAggregateAccumulator value) { _ = value; } }
}
