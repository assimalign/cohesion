namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Creates an empty SQL namespace.</summary>
internal sealed record SqlCreateSchemaPlan(string Name, bool IfNotExists) : SqlPlan;
