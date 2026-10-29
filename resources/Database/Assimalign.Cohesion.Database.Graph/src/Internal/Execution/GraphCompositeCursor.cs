using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// One explicit-stack frame. All data is execution-local; no recursive iterator chain is built.
internal sealed class GraphCompositeCursor
{
    public GraphCompositeCursor(GraphCompositeNode node, GraphQueryMemory memory)
    {
        Node = node;
        Memory = new GraphMemoryOwner(memory);
        if (node.Operator == GqlCompositeOperator.Union) { Seen = new(GraphRowComparer.Instance); }
        if (node.Operator is GqlCompositeOperator.Except or GqlCompositeOperator.ExceptAll or
            GqlCompositeOperator.Intersect or GqlCompositeOperator.IntersectAll)
        { Counts = new(GraphRowComparer.Instance); Rows = []; }
    }

    public GraphCompositeNode Node { get; }
    public GraphMemoryOwner Memory { get; }
    public IAsyncEnumerator<object?[]>? Source { get; set; }
    public HashSet<object?[]>? Seen { get; }
    public Dictionary<object?[], (long Available, long Matched)>? Counts { get; }
    public List<object?[]>? Rows { get; }
    public GraphCompositeCursorPhase Phase { get; set; }
    public int OutputIndex { get; set; }
    public bool HasRows { get; set; }
}

internal enum GraphCompositeCursorPhase
{
    BeginLeft,
    ReadLeft,
    BeginRight,
    ReadRight,
    Emit,
    Complete,
}
