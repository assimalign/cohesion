using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlDropIndexPlan(SqlCatalogTable Table, string IndexName, bool IfExists) : SqlPlan;
