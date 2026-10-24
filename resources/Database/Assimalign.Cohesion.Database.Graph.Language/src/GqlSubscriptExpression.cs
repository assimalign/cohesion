using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A list element or map field reference.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlSubscriptExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlSubscriptExpression.DebugView))]
public sealed class GqlSubscriptExpression : GqlExpression
{
    /// <summary>Initializes the expression.</summary>
    /// <param name="operand">The list or map.</param>
    /// <param name="index">The integer index or string key.</param>
    /// <param name="location">The optional source span.</param>
    public GqlSubscriptExpression(GqlExpression operand, GqlExpression index, Location? location = null) : base(location)
    {
        Operand = operand;
        Index = index;
    }

    /// <summary>Gets the container.</summary>
    public GqlExpression Operand { get; }
    /// <summary>Gets the index expression.</summary>
    public GqlExpression Index { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlSubscriptExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlSubscriptExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Operand => _target.Operand;
        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Index => _target.Index;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
