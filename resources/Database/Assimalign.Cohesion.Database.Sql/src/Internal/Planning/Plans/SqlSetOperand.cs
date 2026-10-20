using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlSetOperand(SqlPlan Query, IReadOnlyList<SqlBoundExpression> Values);
