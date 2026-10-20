using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlDropConstraintPlan(SqlCatalogTable Table, string ConstraintName) : SqlPlan;
