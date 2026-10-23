using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A nested property or array-element lookup.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlPathExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlPathExpression.DebugView))]
public sealed class OqlPathExpression : OqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="OqlPathExpression"/> class.</summary>
    /// <param name="segments">The path steps, including any explicit iteration variable.</param>
    /// <param name="location">The source span.</param>
    public OqlPathExpression(IReadOnlyList<OqlPathSegment> segments, Location? location = null) : base(location)
    {
        Segments = segments;
    }

    /// <summary>Gets the path steps in traversal order.</summary>
    public IReadOnlyList<OqlPathSegment> Segments { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlPathExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlPathExpression value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlPathSegment> Segments => _target.Segments;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
