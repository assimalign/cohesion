using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A database-scoped SQL function or domain DDL statement.</summary>
internal sealed record SqlDefinitionPlan(SqlQueryExpression Expression) : SqlPlan;
