using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Collects a projection for each match of a lexically isolated, correlated path pattern.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlPatternComprehensionExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(GqlPatternComprehensionExpression.DebugView))]
public sealed class GqlPatternComprehensionExpression : GqlExpression
{
    /// <summary>Initializes a pattern comprehension.</summary>
    /// <param name="pattern">The nonempty path pattern, with an optional local path variable.</param>
    /// <param name="predicate">The optional match filter; only TRUE keeps a match.</param>
    /// <param name="projection">The value collected for each retained match.</param>
    /// <param name="location">The optional source span.</param>
    public GqlPatternComprehensionExpression(GqlPathPattern pattern, GqlExpression? predicate,
        GqlExpression projection, Location? location = null) : base(location)
    { Pattern = pattern; Predicate = predicate; Projection = projection; }

    /// <summary>Gets the inner pattern. Its path name is local; other existing names correlate.</summary>
    public GqlPathPattern Pattern { get; }
    /// <summary>Gets the optional filter.</summary>
    public GqlExpression? Predicate { get; }
    /// <summary>Gets the collected projection.</summary>
    public GqlExpression Projection { get; }
    /// <summary>Renders equivalent GQL syntax.</summary>
    /// <returns>The pattern comprehension.</returns>
    public override string ToString() => GqlPathRenderer.RenderExpression(this);

    private sealed class DebugView
    {
        private readonly GqlPatternComprehensionExpression _target;
        public DebugView(GqlPatternComprehensionExpression value) { _target = value; }
        public GqlPathPattern Pattern => _target.Pattern;
        public GqlExpression? Predicate => _target.Predicate;
        public GqlExpression Projection => _target.Projection;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
