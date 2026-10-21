using System.Collections.Generic;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentLogicalPlan(OqlSelectExpression Query, IReadOnlyList<DocumentProjection> Projections, bool IsGrouped,
    long? Limit, long Offset, IReadOnlySet<string>? UnnestVariables, bool CanStopEarly, IReadOnlyList<bool> ProjectedOrdering);
