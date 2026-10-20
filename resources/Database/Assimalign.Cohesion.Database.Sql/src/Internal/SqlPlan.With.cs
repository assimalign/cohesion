using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Materializes the reachable WITH definitions before the statement's first write.</summary>
internal sealed record SqlWithPlan(SqlPlan Input, IReadOnlyList<SqlDerivedRelation> Relations) : SqlPlan;
