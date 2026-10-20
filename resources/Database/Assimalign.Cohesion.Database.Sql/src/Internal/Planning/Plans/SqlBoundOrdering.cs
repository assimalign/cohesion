namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>One bound <c>ORDER BY</c> key.</summary>
/// <param name="Key">The bound key, over the source row, or over the row extended by the completed outputs when the plan orders by them.</param>
/// <param name="IsDescending">Whether the key sorts descending.</param>
/// <param name="NullsFirst">True when NULL sorts before non-NULL values; binding resolves the default placement.</param>
/// <param name="Collation">The collation the key's values compare under.</param>
internal sealed record SqlBoundOrdering(SqlBoundExpression Key, bool IsDescending, bool NullsFirst, SqlBoundCollation Collation);
