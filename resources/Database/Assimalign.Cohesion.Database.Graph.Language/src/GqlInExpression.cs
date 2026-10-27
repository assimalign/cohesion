using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A list membership predicate with a list-valued right operand.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlInExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlInExpression.DebugView))]
public sealed class GqlInExpression : GqlExpression
{
    /// <summary>Initializes an expression.</summary>
    /// <param name="operand">The value to find.</param>
    /// <param name="list">The list-valued expression.</param>
    /// <param name="location">The source span.</param>
    public GqlInExpression(GqlExpression operand, GqlExpression list, Location? location = null) : base(location)
    {
        Operand = operand;
        List = list;
    }

    /// <summary>Gets the value to find.</summary>
    public GqlExpression Operand { get; }
    /// <summary>Gets the list-valued expression.</summary>
    public GqlExpression List { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlInExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlInExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Operand => _target.Operand;
        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression List => _target.List;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
