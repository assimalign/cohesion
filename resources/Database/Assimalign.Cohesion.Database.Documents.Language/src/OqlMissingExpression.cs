using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The SQL++ MISSING value, distinct from the NULL literal.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlMissingExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlMissingExpression.DebugView))]
public sealed class OqlMissingExpression : OqlExpression
{
    /// <summary>Initializes a MISSING expression.</summary>
    /// <param name="location">The source span, or null for a programmatically constructed node.</param>
    public OqlMissingExpression(Location? location = null) : base(location) { }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlMissingExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlMissingExpression value)
        {
            _target = value;
        }

        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
