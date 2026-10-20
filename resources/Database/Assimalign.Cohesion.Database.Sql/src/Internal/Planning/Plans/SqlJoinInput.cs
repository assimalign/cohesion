namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A binding's constant access path and restriction over the stable joined ordinals.</summary>
internal sealed record SqlJoinInput(SqlAccessPath Access, SqlBoundExpression? Filter);
