namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A grouping key bound over the input row, with the collation its values are grouped under.</summary>
/// <param name="Value">The bound key expression.</param>
/// <param name="Collation">The collation the key's values compare and hash under.</param>
internal sealed record SqlBoundKey(SqlBoundExpression Value, SqlBoundCollation Collation);
