using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Expands a list using FOR or Cypher UNWIND.</summary>
/// <param name="Variable">The new element variable.</param>
/// <param name="List">The list expression.</param>
/// <param name="IsUnwind">Whether the source used UNWIND.</param>
/// <param name="Location">The source span.</param>
public sealed record GqlForStage(string Variable, GqlExpression List, bool IsUnwind = false, Location? Location = null) : GqlStage(Location);
