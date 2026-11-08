using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The matched UPDATE action, reusing ordinary SET and UNSET paths.</summary>
/// <param name="Assignments">The ordered SET assignments.</param>
/// <param name="Unset">The ordered UNSET paths.</param>
/// <param name="Predicate">The optional action WHERE expression.</param>
public sealed record OqlMergeUpdateAction(IReadOnlyList<OqlUpdateAssignment> Assignments,
    IReadOnlyList<OqlPathExpression> Unset, OqlExpression? Predicate = null);
