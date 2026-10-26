using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Mandatory, safe equality keys; the original ON remains the residual.</summary>
internal sealed record SqlHashJoinPath(int[] OuterOrdinals, int[] InnerOrdinals, Collation[] Collations,
    SqlBoundExpression[]? OuterExpressions = null, SqlBoundExpression[]? InnerExpressions = null);
