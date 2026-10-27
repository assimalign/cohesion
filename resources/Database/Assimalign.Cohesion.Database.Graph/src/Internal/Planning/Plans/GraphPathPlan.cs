using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[global::System.Diagnostics.DebuggerDisplay("GraphPathPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPathPlan.DebugView))]
internal sealed record GraphPathPlan(GqlPathPattern Pattern, GraphAnchor Anchor)
{
    internal GraphPathShape? Shape { get; init; }
    internal GraphPathSelectionPlan? Selection { get; init; }
    internal GqlPathMode Mode => Pattern.Mode ?? GqlPathMode.Trail;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Internal.GraphPathPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPathPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlPathPattern Pattern => _target.Pattern;
        public global::Assimalign.Cohesion.Database.Graph.Internal.GraphAnchor Anchor => _target.Anchor;
    }
}
