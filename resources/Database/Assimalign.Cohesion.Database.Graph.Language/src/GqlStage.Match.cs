using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Matches or null-extends each incoming binding.</summary>
/// <param name="Clause">The matching clause.</param>
/// <param name="Location">The source span.</param>
public sealed record GqlMatchStage(GqlMatchClause Clause, Location? Location = null) : GqlStage(Location);
