using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Registers children that consume enclosing rows; each evaluation frame owns its value slots.</summary>
internal sealed record SqlCorrelatedSubqueryPlan(SqlPlan Input, IReadOnlyList<SqlSubqueryBinding> Queries) : SqlPlan;
