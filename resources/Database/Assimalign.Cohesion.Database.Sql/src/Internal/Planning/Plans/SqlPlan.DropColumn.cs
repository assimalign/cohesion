namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlDropColumnPlan(string Schema, string Name, string ColumnName) : SqlPlan;
