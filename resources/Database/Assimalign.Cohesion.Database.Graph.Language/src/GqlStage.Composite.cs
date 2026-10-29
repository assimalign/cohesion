using System;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A composite binding-table operation within a NEXT pipeline.</summary>
public sealed record GqlCompositeStage : GqlStage
{
    /// <summary>Initializes a composite pipeline stage.</summary>
    /// <param name="query">The grouped composite query.</param>
    /// <param name="location">The source span.</param>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    public GqlCompositeStage(GqlCompositeQuery query, Location? location = null) : base(location)
    {
        ArgumentNullException.ThrowIfNull(query);
        Query = query;
    }

    /// <summary>Gets the composite operation evaluated against the incoming bindings.</summary>
    public GqlCompositeQuery Query { get; }
}
