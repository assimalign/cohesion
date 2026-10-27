using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An explicit scalar conversion with SQL-compatible value validation.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlCastExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlCastExpression.DebugView))]
public sealed class GqlCastExpression : GqlExpression
{
    /// <summary>Initializes an explicit conversion.</summary>
    /// <param name="operand">The value to convert.</param>
    /// <param name="target">The resolved target type.</param>
    /// <param name="location">The complete source span.</param>
    public GqlCastExpression(GqlExpression operand, GqlCastType target, Location? location = null) : base(location)
    {
        Operand = operand;
        Target = target;
    }

    /// <summary>Gets the operand.</summary>
    public GqlExpression Operand { get; }
    /// <summary>Gets the target type.</summary>
    public GqlCastType Target { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlCastExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlCastExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Operand => _target.Operand;
        public global::Assimalign.Cohesion.Database.Graph.Language.GqlCastType Target => _target.Target;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
