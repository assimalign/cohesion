using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The ISO COALESCE case abbreviation.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlCoalesceExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlCoalesceExpression.DebugView))]
public sealed class GqlCoalesceExpression : GqlExpression
{
    /// <summary>Initializes the expression.</summary>
    /// <param name="operands">The expressions to test in order.</param>
    /// <param name="location">The optional source span.</param>
    public GqlCoalesceExpression(IEnumerable<GqlExpression> operands, Location? location = null) : base(location)
    {
        Operands = operands is null ? null! : System.Array.AsReadOnly(operands.ToArray());
    }

    /// <summary>Gets the expressions in evaluation order.</summary>
    public IReadOnlyList<GqlExpression> Operands { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlCoalesceExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlCoalesceExpression value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression> Operands => _target.Operands;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
