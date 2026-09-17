using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A node in the OQL expression tree.</summary>
public abstract class OqlExpression : QueryExpression
{
    /// <summary>Initializes an expression with its source location.</summary>
    /// <param name="location">The source span, with an exclusive end offset.</param>
    protected OqlExpression(Location? location = null) => Location = location;

    /// <inheritdoc />
    public override Location? Location { get; }
}
