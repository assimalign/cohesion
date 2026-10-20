namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Drops a SQL namespace and optionally its dependent objects.</summary>
internal sealed record SqlDropSchemaPlan(string Name, bool IfExists, bool Cascade) : SqlPlan;
