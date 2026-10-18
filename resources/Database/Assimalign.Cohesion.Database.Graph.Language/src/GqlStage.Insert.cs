using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Inserts patterns for every incoming binding.</summary>
/// <param name="Paths">The patterns to insert.</param>
/// <param name="IsCreate">Whether the source used CREATE.</param>
/// <param name="Location">The source span.</param>
public sealed record GqlInsertStage(IReadOnlyList<GqlPathPattern> Paths, bool IsCreate = false, Location? Location = null) : GqlStage(Location);
