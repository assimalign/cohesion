using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[global::System.Diagnostics.DebuggerDisplay("GraphBindingShape")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphBindingShape.DebugView))]
internal sealed record GraphBindingShape(bool[] Entities, bool[] Composites, bool[] Scalars, GraphEntityShape[] EntityKinds, DatabaseType Type)
{
    internal bool? ResultNullable { get; init; }
    internal bool[] Groups { get; init; } = [];
    internal bool Group => Groups.Length > 0 && Groups[0];
    internal GraphFunctionKind ValueKind { get; init; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Internal.GraphBindingShape _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphBindingShape value)
        {
            _target = value;
        }

        public bool[] Entities => _target.Entities;
        public bool[] Composites => _target.Composites;
        public bool[] Scalars => _target.Scalars;
        public global::Assimalign.Cohesion.Database.Graph.Internal.GraphEntityShape[] EntityKinds => _target.EntityKinds;
        public global::Assimalign.Cohesion.Database.Types.DatabaseType Type => _target.Type;
    }
}
