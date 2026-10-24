using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A field reference on a map-valued expression.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlMemberExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlMemberExpression.DebugView))]
public sealed class GqlMemberExpression : GqlExpression
{
    /// <summary>Initializes the expression.</summary>
    /// <param name="operand">The map expression.</param>
    /// <param name="member">The ordinal field name.</param>
    /// <param name="location">The optional source span.</param>
    public GqlMemberExpression(GqlExpression operand, string member, Location? location = null) : base(location)
    {
        Operand = operand;
        Member = member;
    }

    /// <summary>Gets the map expression.</summary>
    public GqlExpression Operand { get; }
    /// <summary>Gets the field name.</summary>
    public string Member { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlMemberExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlMemberExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Operand => _target.Operand;
        public string Member => _target.Member;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
