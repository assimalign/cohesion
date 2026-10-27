using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A supported scalar or aggregate function call.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlCallExpression {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlCallExpression.DebugView))]
public sealed class OqlCallExpression : OqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="OqlCallExpression"/> class.</summary>
    /// <param name="name">The uppercase function name.</param>
    /// <param name="arguments">The function arguments.</param>
    /// <param name="location">The source span.</param>
    /// <param name="quantifier">The optional aggregate input quantifier.</param>
    public OqlCallExpression(string name, IReadOnlyList<OqlExpression> arguments, Location? location = null, OqlAggregateQuantifier quantifier = OqlAggregateQuantifier.None) : base(location)
    {
        Name = name;
        Arguments = arguments;
        Quantifier = quantifier;
    }

    /// <summary>Gets the uppercase function name.</summary>
    public string Name { get; }
    /// <summary>Gets the function arguments.</summary>
    public IReadOnlyList<OqlExpression> Arguments { get; }

    /// <summary>Gets the aggregate input quantifier, or None for an unquantified call.</summary>
    public OqlAggregateQuantifier Quantifier { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlCallExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlCallExpression value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public OqlAggregateQuantifier Quantifier => _target.Quantifier;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression> Arguments => _target.Arguments;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
