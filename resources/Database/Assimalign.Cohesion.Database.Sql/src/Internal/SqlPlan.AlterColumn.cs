using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlAlterColumnPlan(SqlCatalogTable Table, SqlCatalogColumn Column, bool AllowDefaultChange = false) : SqlPlan;
