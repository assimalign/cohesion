namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Drops a persisted view and optionally its dependent views.</summary>
internal sealed record SqlDropViewPlan(string Schema, string Name, bool IfExists, bool Cascade) : SqlPlan;
