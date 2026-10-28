using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An ordering expression, direction and nullish placement.</summary>
/// <param name="Expression">The ordering expression.</param>
/// <param name="Descending">Whether larger values sort first.</param>
/// <param name="NullPlacement">The written NULLS option; direction determines the omitted default.</param>
public sealed record OqlOrdering(OqlExpression Expression, bool Descending,
    OqlNullPlacement NullPlacement = OqlNullPlacement.Default);
