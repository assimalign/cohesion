using System;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>The transaction instant and statement snapshot, passed explicitly to every evaluator.</summary>
internal readonly record struct GraphEvaluationContext(DateTimeOffset TransactionStart, GraphDatabase? Database = null, GraphOperation? Operation = null)
{
    internal object? Current(object? value) => Operation is null ? value : Operation.Updates.Current(value);

    internal GraphNode Endpoint(GraphRelationship relationship, bool start)
    {
        ulong id = start ? relationship.From.Value : relationship.To.Value;
        if (Database is null || Operation is null || Operation.Updates.FindNode(id) is not { } node)
        { throw new GraphPlanningException("CG02013", ErrorMessages.FunctionEndpointMissing); }
        return GraphDatabase.Materialize(node);
    }
}
