namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

/// <summary>Only inference-WHERE syntax needs to retain two predicates.</summary>
internal sealed record SqlOnConflictPredicates(SqlExpression Target, SqlExpression? Action);
