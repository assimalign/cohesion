using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The SQL++ MISSING value, distinct from the NULL literal.</summary>
public sealed class OqlMissingExpression : OqlExpression
{
    /// <summary>Initializes a MISSING expression.</summary>
    /// <param name="location">The source span, or null for a programmatically constructed node.</param>
    public OqlMissingExpression(Location? location = null) : base(location) { }
}
