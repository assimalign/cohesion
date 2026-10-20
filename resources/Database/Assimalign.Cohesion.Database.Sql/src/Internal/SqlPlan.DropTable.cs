namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlDropTablePlan(string Schema, string Name, bool IfExists) : SqlPlan;
