using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A nested property or array-element lookup.</summary>
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
}
