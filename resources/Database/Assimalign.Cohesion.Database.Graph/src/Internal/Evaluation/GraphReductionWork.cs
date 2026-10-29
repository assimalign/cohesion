using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[System.Diagnostics.DebuggerDisplay("GraphReductionWork")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphReductionWork.DebugView))]
internal sealed class GraphReductionWork
{
    private readonly CancellationToken _token;
    private int _remaining = 65536;

    internal GraphReductionWork(CancellationToken token) { _token = token; }

    internal void Visit(GqlExpression expression)
    {
        _token.ThrowIfCancellationRequested();
        if (--_remaining < 0)
        { throw new GraphPlanningException("CG05026", ErrorMessages.ReductionPlanningLimit, expression.Location); }
    }

    private sealed class DebugView
    {
        public DebugView(GraphReductionWork value) { }
    }
}
