using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A reference to a bound node, relationship or path.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlVariableExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlVariableExpression.DebugView))]
public sealed class GqlVariableExpression : GqlExpression
{
    /// <summary>Initializes a binding reference.</summary>
    /// <param name="variable">The case-sensitive binding name.</param>
    /// <param name="location">The optional source span.</param>
    public GqlVariableExpression(string variable, Location? location = null) : base(location)
    {
        Variable = variable;
    }

    /// <summary>Gets the binding name.</summary>
    public string Variable { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlVariableExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlVariableExpression value)
        {
            _target = value;
        }

        public string Variable => _target.Variable;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
