using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A scalar property of a bound graph element.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlPropertyExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlPropertyExpression.DebugView))]
public sealed class GqlPropertyExpression : GqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="GqlPropertyExpression"/> class.</summary>
    /// <param name="variable">The bound node or relationship variable.</param>
    /// <param name="property">The property key.</param>
    /// <param name="location">The source span.</param>
    public GqlPropertyExpression(string variable, string property, Location? location = null) : base(location)
    {
        Variable = variable;
        Property = property;
    }

    /// <summary>Gets the binding name.</summary>
    public string Variable { get; }
    /// <summary>Gets the property key.</summary>
    public string Property { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlPropertyExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlPropertyExpression value)
        {
            _target = value;
        }

        public string Variable => _target.Variable;
        public string Property => _target.Property;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
