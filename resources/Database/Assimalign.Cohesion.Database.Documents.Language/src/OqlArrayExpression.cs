using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Constructs a JSON array from expressions in source order.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlArrayExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlArrayExpression.DebugView))]
public sealed class OqlArrayExpression : OqlExpression
{
    /// <summary>Initializes an array constructor.</summary>
    /// <param name="elements">The element expressions; MISSING elements become JSON null.</param>
    /// <param name="location">The source span.</param>
    public OqlArrayExpression(IReadOnlyList<OqlExpression> elements, Location? location = null) : base(location)
    {
        Elements = elements;
    }

    /// <summary>Gets the element expressions in source order.</summary>
    public IReadOnlyList<OqlExpression> Elements { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlArrayExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlArrayExpression value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression> Elements => _target.Elements;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
