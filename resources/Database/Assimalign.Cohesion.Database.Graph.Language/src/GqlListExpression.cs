using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A bracketed list of expressions, usable in every value position.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlListExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlListExpression.DebugView))]
public sealed class GqlListExpression : GqlExpression
{
    /// <summary>Initializes a list over a copy of its items.</summary>
    /// <param name="items">The items in source order.</param>
    /// <param name="location">The source span.</param>
    public GqlListExpression(IEnumerable<GqlExpression> items, Location? location = null) : base(location)
    {
        Items = items is null ? null! : new List<GqlExpression>(items).AsReadOnly();
    }

    /// <summary>Gets the items in source order.</summary>
    public IReadOnlyList<GqlExpression> Items { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlListExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlListExpression value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression> Items => _target.Items;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
