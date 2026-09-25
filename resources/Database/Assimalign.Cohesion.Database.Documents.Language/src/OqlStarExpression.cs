using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The entire source document, or the row-count operand of COUNT(*).</summary>
public sealed class OqlStarExpression : OqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="OqlStarExpression"/> class.</summary>
    /// <param name="location">The source span.</param>
    public OqlStarExpression(Location? location = null) : base(location)
    {
    }
}
