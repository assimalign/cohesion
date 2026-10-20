using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlRenamePlan(SqlCatalogTable Table, string? ColumnName, string NewName) : SqlPlan;
