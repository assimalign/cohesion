using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[global::System.Diagnostics.DebuggerDisplay("GraphProjectionPlan {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphProjectionPlan.DebugView))]
internal sealed record GraphProjectionPlan(GqlExpression Expression, string Name, GraphProjectionKind Kind)
{
    internal string? BindingName { get; init; }
    internal bool MayBeComposite { get; init; }
    internal DatabaseType Type { get; init; }
    internal bool? ResultNullable { get; init; }
    internal bool Optional { get; init; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Internal.GraphProjectionPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphProjectionPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Expression => _target.Expression;
        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.Graph.Internal.GraphProjectionKind Kind => _target.Kind;
    }
}
