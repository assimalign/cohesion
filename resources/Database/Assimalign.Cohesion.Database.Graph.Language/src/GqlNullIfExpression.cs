using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The ISO NULLIF case abbreviation.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlNullIfExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlNullIfExpression.DebugView))]
public sealed class GqlNullIfExpression : GqlExpression
{
    /// <summary>Initializes the expression.</summary>
    /// <param name="left">The result and comparison operand.</param>
    /// <param name="right">The value to compare.</param>
    /// <param name="location">The optional source span.</param>
    public GqlNullIfExpression(GqlExpression left, GqlExpression right, Location? location = null) : base(location)
    {
        Left = left;
        Right = right;
    }

    /// <summary>Gets the first operand.</summary>
    public GqlExpression Left { get; }
    /// <summary>Gets the second operand.</summary>
    public GqlExpression Right { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlNullIfExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlNullIfExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Left => _target.Left;
        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Right => _target.Right;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
