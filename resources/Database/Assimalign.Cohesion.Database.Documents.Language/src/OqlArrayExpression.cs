using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Constructs a JSON array from expressions in source order.</summary>
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
}
