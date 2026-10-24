using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The entire source document, or the row-count operand of COUNT(*).</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlStarExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlStarExpression.DebugView))]
public sealed class OqlStarExpression : OqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="OqlStarExpression"/> class.</summary>
    /// <param name="location">The source span.</param>
    public OqlStarExpression(Location? location = null) : base(location)
    {
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlStarExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlStarExpression value)
        {
            _target = value;
        }

        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
