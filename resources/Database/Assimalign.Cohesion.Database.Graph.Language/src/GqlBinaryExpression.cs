using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A scalar comparison, arithmetic or string concatenation expression.</summary>
/// <remarks>
/// A conjunction is not a binary node: every <c>AND</c> chain is one <see cref="GqlLogicalExpression"/>,
/// and the engine rejects a binary node whose operator is <c>AND</c> with <c>CG05001</c>.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("GqlBinaryExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlBinaryExpression.DebugView))]
public sealed class GqlBinaryExpression : GqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="GqlBinaryExpression"/> class.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="operator">The operator: =, !=, &lt;&gt;, &lt;, &lt;=, &gt;, &gt;=, +, -, *, /, % or ||.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="location">The source span.</param>
    public GqlBinaryExpression(GqlExpression left, string @operator, GqlExpression right,
        Location? location = null) : base(location)
    {
        Left = left;
        Operator = @operator;
        Right = right;
    }

    /// <summary>Gets the left operand.</summary>
    public GqlExpression Left { get; }
    /// <summary>Gets the scalar operator.</summary>
    public string Operator { get; }
    /// <summary>Gets the right operand.</summary>
    public GqlExpression Right { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlBinaryExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlBinaryExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Left => _target.Left;
        public string Operator => _target.Operator;
        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Right => _target.Right;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
