using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An ordering expression and its direction.</summary>
/// <param name="Expression">The ordering expression.</param>
/// <param name="Descending">Whether larger values sort first.</param>
public sealed record OqlOrdering(OqlExpression Expression, bool Descending);
