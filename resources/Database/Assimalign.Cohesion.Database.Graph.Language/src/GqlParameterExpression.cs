using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A named scalar parameter referenced with $ or @.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlParameterExpression {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlParameterExpression.DebugView))]
public sealed class GqlParameterExpression : GqlExpression
{
    /// <summary>Initializes a named parameter expression.</summary>
    /// <param name="name">The bare, case-sensitive name without its sigil.</param>
    /// <param name="location">The complete reference's source span.</param>
    public GqlParameterExpression(string name, Location? location = null) : base(location)
    {
        Name = name;
    }

    /// <summary>Gets the bare, case-sensitive parameter name.</summary>
    public string Name { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlParameterExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlParameterExpression value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
