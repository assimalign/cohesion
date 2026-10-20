using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphBindingShape(bool[] Entities, bool[] Composites, bool[] Scalars, GraphEntityShape[] EntityKinds, DatabaseType Type)
{
    internal bool[] Groups { get; init; } = [];
    internal bool Group => Groups.Length > 0 && Groups[0];
    internal GraphFunctionKind ValueKind { get; init; }
}
