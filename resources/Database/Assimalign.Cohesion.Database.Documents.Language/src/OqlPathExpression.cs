using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A nested property or array-element lookup.</summary>
/// <param name="segments">The path steps, including any explicit iteration variable.</param>
/// <param name="location">The source span.</param>
public sealed class OqlPathExpression(IReadOnlyList<OqlPathSegment> segments, Location? location = null) : OqlExpression(location)
{
    /// <summary>Gets the path steps in traversal order.</summary>
    public IReadOnlyList<OqlPathSegment> Segments { get; } = segments;
}
